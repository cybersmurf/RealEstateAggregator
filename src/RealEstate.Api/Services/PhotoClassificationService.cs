using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Services;

/// <summary>
/// Klasifikuje fotky nemovitostí vision modelem: primárně Gemini Flash Lite přes OpenRouter,
/// záloha Mistral. Výběr vzešel ze srovnání 9 modelů na 23 ručně ověřených fotkách (září 2026):
/// mistral-small přehlédl 2 ze 3 skutečných poškození a 2× si ho vymyslel, Gemini 0/0.
/// Čte soubory z lokálního storage (wwwroot/uploads/...) a posílá jako base64.
/// Výsledky ukládá do sloupců photo_category, photo_labels, damage_detected, classified_at.
/// </summary>
public sealed class PhotoClassificationService(
    RealEstateDbContext db,
    IWebHostEnvironment env,
    IStorageService storageService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PhotoClassificationService> logger) : IPhotoClassificationService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record VisionEndpoint(string Name, string Url, string ApiKey, string Model);

    /// <summary>
    /// Poskytovatelé v pořadí, v jakém se zkouší. Oba mluví OpenAI-kompatibilním chat API.
    /// PHOTO_VISION_PROVIDER=mistral vynutí jen Mistral (např. při výpadku OpenRouteru).
    /// </summary>
    private List<VisionEndpoint> VisionEndpoints
    {
        get
        {
            var endpoints = new List<VisionEndpoint>();
            var forced = Environment.GetEnvironmentVariable("PHOTO_VISION_PROVIDER")
                         ?? configuration["Photos:VisionProvider"];

            var openRouterKey = configuration["OpenRouter:ApiKey"];
            if (!string.IsNullOrWhiteSpace(openRouterKey)
                && !string.Equals(forced, "mistral", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = (configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
                endpoints.Add(new VisionEndpoint("OpenRouter", $"{baseUrl}/chat/completions", openRouterKey,
                    Environment.GetEnvironmentVariable("OPENROUTER_VISION_MODEL")
                    ?? configuration["OpenRouter:VisionModel"]
                    ?? "google/gemini-3.1-flash-lite"));
            }

            var mistralKey = Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? configuration["Mistral:ApiKey"];
            if (!string.IsNullOrWhiteSpace(mistralKey)
                && !string.Equals(forced, "openrouter", StringComparison.OrdinalIgnoreCase))
            {
                endpoints.Add(new VisionEndpoint("Mistral", "https://api.mistral.ai/v1/chat/completions", mistralKey,
                    Environment.GetEnvironmentVariable("MISTRAL_VISION_MODEL")
                    ?? configuration["Mistral:VisionModel"]
                    ?? "mistral-medium-latest"));
            }

            return endpoints.Count > 0
                ? endpoints
                : throw new InvalidOperationException("Není nakonfigurován OpenRouter:ApiKey ani MISTRAL_API_KEY");
        }
    }

    // Prodlevy před opakováním po HTTP 429; po vyčerpání se dávka ukončí
    private static readonly TimeSpan[] _rateLimitRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    private const string RateLimitedMessage =
        "Vision API odmítá požadavky (HTTP 429 – rate limit / vyčerpaná kvóta).";

    // Public base URL odstraníme ze stored_url abychom dostali relativní cestu k souboru
    private string PublicBaseUrl =>
        Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
        ?? configuration["Photos:PublicBaseUrl"]
        ?? "http://localhost:5001";

    // Jeden prompt pro kategorii, štítky, poškození i popis. Dřív to byla dvě nezávislá volání,
    // která si protiřečila (damage_detected=true + popis „no visible defects").
    private const string ClassificationPrompt = """
        You label one photo from a Czech real-estate listing. Report only what is clearly visible in THIS photo. Never guess, never embellish, never invent objects. Ignore watermarks and agency logos.

        Respond with JSON only:
        {"category":"...","labels":[...],"damage_detected":false,"damage_evidence":null,"description":"...","confidence":0.9}

        "category" - exactly one of:
        exterior, interior, kitchen, bathroom, living_room, bedroom, attic, basement, garage, land, floor_plan, damage, other
        (drone/aerial shots of the house -> exterior; gardens, plots, maps of plots -> land; drawings of room layout -> floor_plan)

        "labels" - 0-5 tags, ONLY those you can actually see, from:
        mold, water_damage, crack, broken_windows, damaged_roof, renovation_needed, garden, pool, fireplace, wooden_beams, new_construction, renovated, brick_walls, wooden_construction, panel_building
        An empty array is a good answer. Do not add a tag because it is on the list.

        "damage_detected" - true ONLY for a visible physical defect: missing or peeling plaster, cracks, mold, water stains, rot, broken windows, damaged roof. A dated, unfinished, cluttered or modest room is NOT damage.
        "damage_evidence" - if damage_detected, a short English phrase naming the defect and where it is; otherwise null.
        "description" - 1-2 factual sentences in Czech: what the photo shows, materials, visible condition. No marketing language.
        "confidence" - 0.0 to 1.0
        """;

    public async Task<PhotoClassificationResultDto> ClassifyBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null, bool onlyMyListings = false)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        // Fotky stažené lokálně NEBO s original_url – stačí mít odkud načíst obrázek
        var query = db.ListingPhotos
            .Where(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null)
            .Where(p => listingId == null || p.ListingId == listingId);

        // Filtr na "moje inzeráty" (Liked/ToVisit/Visited)
        if (onlyMyListings)
        {
            query = query.Where(p => db.UserListingStates
                .Any(u => u.ListingId == p.ListingId 
                    && (u.Status == "Liked" 
                        || u.Status == "ToVisit" 
                        || u.Status == "Visited")));
        }

        // Pokud je zadán konkrétní listing, klasifikuj VŠECHNY jeho fotky.
        // BatchSize se aplikuje jen pro globální bulk bez listingId.
        var orderedQuery = query.OrderBy(p => p.ListingId).ThenBy(p => p.Order);
        var photos = await (listingId.HasValue ? orderedQuery : orderedQuery.Take(batchSize))
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remainingQuery0 = db.ListingPhotos
                .Where(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null
                    && (listingId == null || p.ListingId == listingId));
            
            if (onlyMyListings)
            {
                remainingQuery0 = remainingQuery0.Where(p => db.UserListingStates
                    .Any(u => u.ListingId == p.ListingId 
                        && (u.Status == "Liked" 
                            || u.Status == "ToVisit" 
                            || u.Status == "Visited")));
            }

            var remaining0 = await remainingQuery0.CountAsync(ct);
            return new PhotoClassificationResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0;
        string? error = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var httpClient = httpClientFactory.CreateClient("MistralVision");

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // ── Načtení obrázku: přednostně lokální disk, fallback na original_url ──
                byte[] imageBytes;
                if (photo.StoredUrl != null)
                {
                    var localPath = ResolveLocalPath(photo.StoredUrl);
                    if (!File.Exists(localPath))
                    {
                        logger.LogWarning(
                            "Photo file not found on disk for listing {ListingId} order {Order}: {Path}",
                            photo.ListingId, photo.Order, localPath);
                        failed++;
                        continue;
                    }
                    imageBytes = await File.ReadAllBytesAsync(localPath, ct);
                }
                else if (photo.OriginalUrl != null)
                {
                    // Fotka ještě není stažená lokálně → stáhneme ji a uložíme.
                    // Po úspěšném uložení nastavíme StoredUrl a příště se čte z disku.
                    using var dlClient = httpClientFactory.CreateClient();
                    dlClient.Timeout = TimeSpan.FromSeconds(30);
                    try
                    {
                        using var dlResponse = await dlClient.GetAsync(photo.OriginalUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (dlResponse.StatusCode is System.Net.HttpStatusCode.NotFound
                                                  or System.Net.HttpStatusCode.Gone)
                        {
                            logger.LogInformation(
                                "Deleting dead photo record {Order} for listing {ListingId} (classification, HTTP {Status})",
                                photo.Order, photo.ListingId, (int)dlResponse.StatusCode);
                            db.ListingPhotos.Remove(photo);
                            await db.SaveChangesAsync(ct);
                            failed++;
                            continue;
                        }

                        var contentType = dlResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                        var ext = contentType switch
                        {
                            "image/png"  => ".png",
                            "image/webp" => ".webp",
                            "image/gif"  => ".gif",
                            _            => ".jpg",
                        };

                        // Uložíme přes IStorageService a nastavíme StoredUrl
                        await using var downloadStream = await dlResponse.Content.ReadAsStreamAsync(ct);
                        var folder = $"listings/{photo.ListingId}/photos";
                        var fileName = $"{photo.Order}{ext}";
                        var relativePath = await storageService.UploadFileAsync(downloadStream, fileName, folder, ct);
                        photo.StoredUrl = $"/{relativePath.TrimStart('/')}";
                        await db.SaveChangesAsync(ct);

                        logger.LogDebug(
                            "Classification: stored photo {Order} for listing {ListingId} → {Url}",
                            photo.Order, photo.ListingId, photo.StoredUrl);

                        // Načteme z disku pro klasifikaci
                        var localPath = ResolveLocalPath(photo.StoredUrl);
                        imageBytes = await File.ReadAllBytesAsync(localPath, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            "Failed to fetch/store original_url for listing {ListingId} photo {Order}: {Ex}",
                            photo.ListingId, photo.Order, ex.Message);
                        failed++;
                        continue;
                    }
                }
                else
                {
                    failed++;
                    continue;
                }

                var (classification, photoDescription) = await RunClassificationAsync(
                    httpClient, imageBytes, photo.ListingId, photo.Id, CancellationToken.None);

                if (classification == null || string.IsNullOrWhiteSpace(classification.Category))
                {
                    failed++;
                    continue;
                }

                // ── Uložení výsledku do DB ──────────────────────────────────────────
                photo.PhotoCategory = NormalizeCategory(classification.Category);
                photo.PhotoDescription = photoDescription;
                photo.PhotoLabels = classification.Labels?.Count > 0
                    ? JsonSerializer.Serialize(classification.Labels)
                    : null;
                photo.DamageDetected = PhotoDamageValidator.IsConfirmed(
                    classification.DamageDetected, classification.Labels, photo.PhotoCategory,
                    photoDescription, classification.DamageEvidence);
                photo.ClassificationConfidence = Math.Clamp(
                    (decimal)(classification.Confidence ?? 0.0), 0m, 1m);
                photo.ClassifiedAt = DateTime.UtcNow;

                logger.LogDebug(
                    "Classified listing {ListingId} photo {Order}: {Category} | damage={Damage} | desc={Desc}",
                    photo.ListingId, photo.Order, photo.PhotoCategory,
                    photo.DamageDetected,
                    photo.PhotoDescription is { } d ? d[..Math.Min(80, d.Length)] : "–");

                succeeded++;
            }
            catch (VisionRateLimitedException ex)
            {
                logger.LogWarning("{Message} Dávka ukončena po {Done} fotkách.", ex.Message, succeeded + failed);
                error = ex.Message;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Classification failed for listing {ListingId} photo {Order}",
                    photo.ListingId, photo.Order);
                failed++;
            }
        }

        if (succeeded > 0)
            await db.SaveChangesAsync(CancellationToken.None);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;
        
        var remainingQuery = db.ListingPhotos
            .Where(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null
                && (listingId == null || p.ListingId == listingId));
        
        if (onlyMyListings)
        {
            remainingQuery = remainingQuery.Where(p => db.UserListingStates
                .Any(u => u.ListingId == p.ListingId 
                    && (u.Status == "Liked" 
                        || u.Status == "ToVisit" 
                        || u.Status == "Visited")));
        }

        var remaining = await remainingQuery.CountAsync(ct);

        logger.LogInformation(
            "Photo classification batch: {Processed} processed, {Succeeded} OK, {Failed} failed. Remaining: {Remaining}. Avg: {Avg}ms",
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));

        return new PhotoClassificationResultDto(
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0), error);
    }

    public async Task<PhotoClassificationResultDto> ClassifyInspectionBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null, bool onlyMyListings = false)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        var query = db.UserListingPhotos
            .Where(p => p.ClassifiedAt == null)
            .Where(p => listingId == null || p.ListingId == listingId);
        
        // Filtr na "moje inzeráty" (Liked/ToVisit/Visited)
        if (onlyMyListings)
        {
            query = query.Where(p => db.UserListingStates
                .Any(u => u.ListingId == p.ListingId 
                    && (u.Status == "Liked" 
                        || u.Status == "ToVisit" 
                        || u.Status == "Visited")));
        }

        // Pokud je zadán konkrétní listing, klasifikuj VŠECHNY jeho fotky.
        // BatchSize se aplikuje jen pro globální bulk bez listingId.
        var orderedInspQuery = query.OrderBy(p => p.ListingId).ThenBy(p => p.UploadedAt);
        var photos = await (listingId.HasValue ? orderedInspQuery : orderedInspQuery.Take(batchSize))
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remainingQuery0 = db.UserListingPhotos
                .Where(p => p.ClassifiedAt == null
                    && (listingId == null || p.ListingId == listingId));
            
            if (onlyMyListings)
            {
                remainingQuery0 = remainingQuery0.Where(p => db.UserListingStates
                    .Any(u => u.ListingId == p.ListingId 
                        && (u.Status == "Liked" 
                            || u.Status == "ToVisit" 
                            || u.Status == "Visited")));
            }

            var remaining0 = await remainingQuery0.CountAsync(ct);
            return new PhotoClassificationResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0;
        string? error = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var httpClient = httpClientFactory.CreateClient("MistralVision");

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var localPath = ResolveLocalPath(photo.StoredUrl);
                if (!File.Exists(localPath))
                {
                    logger.LogWarning(
                        "Inspection photo not found on disk for listing {ListingId}: {Path}",
                        photo.ListingId, localPath);
                    failed++;
                    continue;
                }

                var imageBytes = await File.ReadAllBytesAsync(localPath, ct);
                var (classification, description) = await RunClassificationAsync(
                    httpClient, imageBytes, photo.ListingId, photo.Id, CancellationToken.None);

                if (classification == null)
                {
                    failed++;
                    continue;
                }

                photo.PhotoCategory          = NormalizeCategory(classification.Category!);
                photo.PhotoLabels            = classification.Labels?.Count > 0
                    ? JsonSerializer.Serialize(classification.Labels) : null;
                photo.DamageDetected         = PhotoDamageValidator.IsConfirmed(
                    classification.DamageDetected, classification.Labels, photo.PhotoCategory,
                    description, classification.DamageEvidence);
                photo.ClassificationConfidence = Math.Clamp(
                    (decimal)(classification.Confidence ?? 0.0), 0m, 1m);
                photo.ClassifiedAt           = DateTime.UtcNow;
                // AiDescription = volný text popis (doplní/přepíše stávající)
                if (description != null)
                    photo.AiDescription = description;

                logger.LogDebug(
                    "Classified inspection photo {PhotoId} listing {ListingId}: {Category} | damage={Damage}",
                    photo.Id, photo.ListingId, photo.PhotoCategory, photo.DamageDetected);

                succeeded++;
            }
            catch (VisionRateLimitedException ex)
            {
                logger.LogWarning("{Message} Dávka ukončena po {Done} fotkách.", ex.Message, succeeded + failed);
                error = ex.Message;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Classification failed for inspection photo {PhotoId} listing {ListingId}",
                    photo.Id, photo.ListingId);
                failed++;
            }
        }

        if (succeeded > 0)
            await db.SaveChangesAsync(CancellationToken.None);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;
        
        var remainingQuery = db.UserListingPhotos
            .Where(p => p.ClassifiedAt == null
                && (listingId == null || p.ListingId == listingId));
        
        if (onlyMyListings)
        {
            remainingQuery = remainingQuery.Where(p => db.UserListingStates
                .Any(u => u.ListingId == p.ListingId 
                    && (u.Status == "Liked" 
                        || u.Status == "ToVisit" 
                        || u.Status == "Visited")));
        }

        var remaining = await remainingQuery.CountAsync(ct);

        logger.LogInformation(
            "Inspection photo classification: {Processed} processed, {Succeeded} OK, {Failed} failed. Remaining: {Remaining}. Avg: {Avg}ms",
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));

        return new PhotoClassificationResultDto(
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0), error);
    }

    /// <summary>
    /// Sdílená vision logika pro oba typy fotek (listing + inspection).
    /// Vrátí (classification, description) nebo (null, null) při selhání.
    /// </summary>
    private async Task<(PhotoClassificationJson? Classification, string? Description)> RunClassificationAsync(
        HttpClient httpClient, byte[] imageBytes, Guid listingId, Guid photoId, CancellationToken ct)
    {
        var base64 = Convert.ToBase64String(imageBytes);

        var classifyRaw = await CallVisionAsync(httpClient, base64, ClassificationPrompt, 400, 0, jsonMode: true, ct);
        if (classifyRaw is null)
        {
            logger.LogWarning("Vision classify call failed for {ListingId}/{PhotoId}", listingId, photoId);
            return (null, null);
        }

        PhotoClassificationJson? classification = null;
        try { classification = JsonSerializer.Deserialize<PhotoClassificationJson>(classifyRaw, _jsonOptions); }
        catch (JsonException) { classification = TryParsePartialJson(classifyRaw); }

        if (classification == null || string.IsNullOrWhiteSpace(classification.Category))
        {
            logger.LogWarning("Could not parse classification for {ListingId}/{PhotoId}: {Raw}",
                listingId, photoId, classifyRaw[..Math.Min(200, classifyRaw.Length)]);
            return (null, null);
        }

        var description = string.IsNullOrWhiteSpace(classification.Description)
            ? null
            : TrimToSentence(classification.Description.Trim(), maxLength: 400);

        return (classification, description);
    }

    /// <summary>
    /// Jeden vision call – vrátí surový text odpovědi (code fences odstraněny).
    /// Poskytovatele zkouší v pořadí z <see cref="VisionEndpoints"/>; na dalšího přejde při chybě
    /// i při vyčerpaném rate limitu. Teprve když 429 vrátí poslední, dávka končí.
    /// </summary>
    private async Task<string?> CallVisionAsync(
        HttpClient httpClient, string base64, string prompt,
        int maxTokens, double temperature, bool jsonMode, CancellationToken ct)
    {
        var endpoints = VisionEndpoints;
        for (var i = 0; i < endpoints.Count; i++)
        {
            var isLast = i == endpoints.Count - 1;
            try
            {
                var result = await CallVisionEndpointAsync(
                    httpClient, endpoints[i], base64, prompt, maxTokens, temperature, jsonMode, ct);
                if (result is not null || isLast) return result;
            }
            catch (VisionRateLimitedException) when (!isLast)
            {
                // přejdeme na zálohu
            }
            catch (HttpRequestException ex) when (!isLast)
            {
                logger.LogWarning("{Provider} Vision nedostupné: {Message}", endpoints[i].Name, ex.Message);
            }

            logger.LogInformation("Vision: {Provider} selhal, zkouším {Next}", endpoints[i].Name, endpoints[i + 1].Name);
        }

        return null;
    }

    private async Task<string?> CallVisionEndpointAsync(
        HttpClient httpClient, VisionEndpoint endpoint, string base64, string prompt,
        int maxTokens, double temperature, bool jsonMode, CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = endpoint.Model,
            ["messages"] = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64}" } },
                        new { type = "text", text = prompt }
                    }
                }
            },
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
        };
        if (jsonMode)
            requestBody["response_format"] = new { type = "json_object" };

        var json = JsonSerializer.Serialize(requestBody);
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

            response = await httpClient.SendAsync(request, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                break;

            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();

            // Krátký limit (req/s) přejde po pár sekundách; vyčerpaná kvóta ne → dávku ukončíme
            if (attempt >= _rateLimitRetryDelays.Length)
                throw new VisionRateLimitedException();

            var delay = retryAfter is { } ra && ra <= TimeSpan.FromSeconds(10) ? ra : _rateLimitRetryDelays[attempt];
            logger.LogInformation("{Provider} Vision HTTP 429, retry {Attempt} za {Delay}s",
                endpoint.Name, attempt + 1, delay.TotalSeconds);
            await Task.Delay(delay, ct);
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(CancellationToken.None);
            logger.LogWarning("{Provider} Vision HTTP {Status}: {Body}",
                endpoint.Name, (int)response.StatusCode, errBody[..Math.Min(300, errBody.Length)]);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var resp = JsonSerializer.Deserialize<MistralChatResponse>(body, _jsonOptions);
        var rawContent = resp?.Choices?[0]?.Message?.Content;
        return rawContent is null ? null : StripCodeFences(rawContent);
    }

    public async Task<PhotoClassificationStatsDto> GetClassificationStatsAsync(CancellationToken ct)
    {
        var total       = await db.ListingPhotos.CountAsync(p => p.StoredUrl != null || p.OriginalUrl != null, ct);
        var classified  = await db.ListingPhotos.CountAsync(p => p.ClassifiedAt != null, ct);
        var withDamage  = await db.ListingPhotos.CountAsync(p => p.DamageDetected, ct);
        var unclassified = total - classified;
        var pct = total > 0 ? Math.Round(classified / (double)total * 100, 1) : 0.0;
        return new PhotoClassificationStatsDto(total, classified, unclassified, withDamage, pct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Převede stored_url (http://localhost:5001/uploads/...) na lokální cestu na disku.
    /// </summary>
    private string ResolveLocalPath(string storedUrl)
    {
        var baseUrl = PublicBaseUrl.TrimEnd('/');

        // Odebereme base URL prefix → "uploads/listings/{id}/photos/0.jpg"
        var relativePath = storedUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
            ? storedUrl[(baseUrl.Length + 1)..]  // +1 za lomítko
            : storedUrl.TrimStart('/');

        // Nahradíme lomítka platformním separátorem a připojíme k wwwroot
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.Combine([env.WebRootPath, .. segments]);
    }

    /// <summary>
    /// Odstraní opakující se věty (LLM hallucination) a zkrátí na max délku.
    /// </summary>
    private static string TrimToSentence(string text, int maxLength)
    {
        // Deduplikace vět (model opakuje věty)
        var sentences = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new System.Text.StringBuilder();
        foreach (var s in sentences)
        {
            // Normalizujeme větu (lowercase, bez interpunkce) pro porovnání
            var key = System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim();
            if (key.Length < 10 || !seen.Add(key)) continue;  // skip duplicits a krátké fragmenty
            if (clean.Length + s.Length + 2 > maxLength) break;
            clean.Append(s.Trim()).Append(". ");
        }

        var result = clean.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(result)
            ? text[..Math.Min(maxLength, text.Length)]
            : result;
    }

    /// <summary>
    /// Zkusí extrahovat klasifikaci z neúplného JSON (Ollama někdy usekne výstup).
    /// Používá regex – stačí nám aspoň category a damage_detected.
    /// </summary>
    private static PhotoClassificationJson? TryParsePartialJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var categoryMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"""category""\s*:\s*""([^""]+)""");
        if (!categoryMatch.Success) return null;

        var descMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"""description""\s*:\s*""((?:[^""\\]|\\.)*)");
        var damageMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"""damage_detected""\s*:\s*(true|false)");
        var confMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"""confidence""\s*:\s*([0-9.]+)");
        // Štítky potřebuje PhotoDamageValidator – bez nich by useknutý JSON poškození nikdy nepotvrdil
        var labelsMatch = System.Text.RegularExpressions.Regex.Match(
            raw, @"""labels""\s*:\s*\[([^\]]*)");

        return new PhotoClassificationJson
        {
            Category = categoryMatch.Groups[1].Value,
            DamageEvidence = System.Text.RegularExpressions.Regex.Match(
                raw, @"""damage_evidence""\s*:\s*""((?:[^""\\]|\\.)*)") is { Success: true } ev
                ? ev.Groups[1].Value : null,
            Labels = labelsMatch.Success
                ? System.Text.RegularExpressions.Regex.Matches(labelsMatch.Groups[1].Value, @"""([^""]+)""")
                    .Select(m => m.Groups[1].Value).ToList()
                : null,
            // Popis může být zkrácený – to je OK, lepší než nic
            Description = descMatch.Success ? descMatch.Groups[1].Value.Trim() : null,
            DamageDetected = damageMatch.Success &&
                             string.Equals(damageMatch.Groups[1].Value, "true",
                                 StringComparison.OrdinalIgnoreCase),
            Confidence = confMatch.Success && double.TryParse(
                confMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var conf) ? conf : null,
        };
    }

    /// <summary>
    /// Normalizuje kategorii – pokud model vrátí neznámou hodnotu, fallback na "other".
    /// </summary>
    private static string NormalizeCategory(string raw)
    {
        var lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "exterior" or "interior" or "kitchen" or "bathroom" or "living_room"
                or "bedroom" or "attic" or "basement" or "garage"
                or "land" or "floor_plan" or "damage" or "other" => lower,
            // Tolerujeme variace
            "livingroom" or "living room" => "living_room",
            "floorplan" or "floor plan" or "plan" => "floor_plan",
            _ => "other",
        };
    }

    // ── Sort by category ──────────────────────────────────────────────────────

    /// <summary>
    /// Priorita kategorie pro řazení fotek:
    /// exteriér první, pak obytné místnosti, technické prostory, půdorysy, poškození nakonec.
    /// </summary>
    private static readonly Dictionary<string, int> _categoryOrder = new()
    {
        ["exterior"]    = 1,
        ["land"]        = 2,
        ["interior"]    = 3,
        ["living_room"] = 4,
        ["kitchen"]     = 5,
        ["bathroom"]    = 6,
        ["bedroom"]     = 7,
        ["attic"]       = 8,
        ["basement"]    = 9,
        ["garage"]      = 10,
        ["floor_plan"]  = 11,
        ["damage"]      = 12,
        ["other"]       = 99,
    };

    public async Task<PhotoSortResultDto> SortByCategoryAsync(Guid listingId, CancellationToken ct)
    {
        var photos = await db.ListingPhotos
            .Where(p => p.ListingId == listingId && p.PhotoCategory != null)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);

        if (photos.Count == 0)
            return new PhotoSortResultDto(listingId, 0, "Žádné klasifikované fotky nenalezeny.");

        // Přiřaď nové pořadí dle priority kategorie, zachovej stabilitu (původní Order jako tiebreaker)
        var sorted = photos
            .OrderBy(p => _categoryOrder.GetValueOrDefault(p.PhotoCategory!, 99))
            .ThenBy(p => p.Order)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
            sorted[i].Order = i + 1;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("SortByCategory: listing {ListingId} → {Count} fotek přeřazeno.", listingId, photos.Count);
        return new PhotoSortResultDto(listingId, photos.Count,
            $"Přeřazeno {photos.Count} fotek dle kategorie (exteriér → obývák → kuchyň → ...).");
    }

    // ── Bulk alt text generation ──────────────────────────────────────────────

    private const string AltTextPrompt =
        "Generate a short, descriptive alt text for this real estate photo in Czech." +
        " The text should be concise (max 100 characters), describe what is visible," +
        " and be suitable as an HTML img alt attribute for screen readers." +
        " Do NOT start with 'Fotka' or 'Obrázek'. Just describe what you see." +
        " Example: 'Obývací pokoj s dřevěnou podlahou a oknem do zahrady'";

    public async Task<PhotoClassificationResultDto> BulkAltTextAsync(
        int batchSize, CancellationToken ct, Guid? listingId = null)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        // Pokud je zadán konkrétní listing, generuj alt text pro VŠECHNY jeho fotky.
        var altQuery = db.ListingPhotos
            .Where(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.AltText == null)
            .Where(p => listingId == null || p.ListingId == listingId)
            .OrderBy(p => p.ListingId).ThenBy(p => p.Order);
        var photos = await (listingId.HasValue ? altQuery : altQuery.Take(batchSize))
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var rem0 = await db.ListingPhotos
                .CountAsync(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.AltText == null
                    && (listingId == null || p.ListingId == listingId), ct);
            return new PhotoClassificationResultDto(0, 0, 0, rem0, 0);
        }

        int succeeded = 0, failed = 0;
        string? error = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var httpClient = httpClientFactory.CreateClient("MistralVision");

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                byte[] imageBytes;
                if (photo.StoredUrl != null)
                {
                    var localPath = ResolveLocalPath(photo.StoredUrl);
                    if (!File.Exists(localPath)) { failed++; continue; }
                    imageBytes = await File.ReadAllBytesAsync(localPath, ct);
                }
                else if (photo.OriginalUrl != null)
                {
                    using var dlClient = httpClientFactory.CreateClient();
                    dlClient.Timeout = TimeSpan.FromSeconds(30);
                    try
                    {
                        using var dlResponse = await dlClient.GetAsync(photo.OriginalUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (dlResponse.StatusCode is System.Net.HttpStatusCode.NotFound
                                                  or System.Net.HttpStatusCode.Gone)
                        {
                            logger.LogInformation(
                                "Deleting dead photo record {Order} for listing {ListingId} (alt-text, HTTP {Status})",
                                photo.Order, photo.ListingId, (int)dlResponse.StatusCode);
                            db.ListingPhotos.Remove(photo);
                            await db.SaveChangesAsync(ct);
                            failed++;
                            continue;
                        }

                        var ct2 = dlResponse.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                        var ext2 = ct2 switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
                        await using var dlStream = await dlResponse.Content.ReadAsStreamAsync(ct);
                        var rel = await storageService.UploadFileAsync(dlStream, $"{photo.Order}{ext2}", $"listings/{photo.ListingId}/photos", ct);
                        photo.StoredUrl = $"/{rel.TrimStart('/')}";
                        await db.SaveChangesAsync(ct);

                        var lp = ResolveLocalPath(photo.StoredUrl);
                        imageBytes = await File.ReadAllBytesAsync(lp, ct);
                    }
                    catch { failed++; continue; }
                }
                else { failed++; continue; }

                var base64 = Convert.ToBase64String(imageBytes);
                var altRaw = await CallVisionAsync(httpClient, base64, AltTextPrompt, 80, 0.2, jsonMode: false, ct);

                if (string.IsNullOrWhiteSpace(altRaw)) { failed++; continue; }

                var altText = altRaw.Trim();
                // Ořízni na max 150 znaků
                if (altText.Length > 150) altText = altText[..150].TrimEnd();
                photo.AltText = altText;
                succeeded++;

                logger.LogDebug("AltText listing {ListingId} photo {Order}: {Alt}",
                    photo.ListingId, photo.Order, altText[..Math.Min(80, altText.Length)]);
            }
            catch (VisionRateLimitedException ex)
            {
                logger.LogWarning("{Message} Dávka ukončena po {Done} fotkách.", ex.Message, succeeded + failed);
                error = ex.Message;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "AltText failed for listing {ListingId} photo {Order}",
                    photo.ListingId, photo.Order);
                failed++;
            }
        }

        if (succeeded > 0) await db.SaveChangesAsync(ct);

        sw.Stop();
        var avgMs = photos.Count > 0 ? sw.ElapsedMilliseconds / (double)photos.Count : 0;
        var remaining = await db.ListingPhotos
            .CountAsync(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.AltText == null
                && (listingId == null || p.ListingId == listingId), ct);

        logger.LogInformation("AltText batch: {Ok}/{Proc} OK. Remaining: {Rem}. Avg: {Avg:F0}ms",
            succeeded, photos.Count, remaining, avgMs);

        return new PhotoClassificationResultDto(photos.Count, succeeded, failed, remaining, avgMs, error);
    }

    // ── Interní deserialization modely ───────────────────────────────────────

    private sealed class VisionRateLimitedException() : Exception(RateLimitedMessage);

    private sealed class MistralChatResponse
    {
        [JsonPropertyName("choices")]
        public MistralChoice[]? Choices { get; set; }
    }

    private sealed class MistralChoice
    {
        [JsonPropertyName("message")]
        public MistralMessage? Message { get; set; }
    }

    private sealed class MistralMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    /// <summary>
    /// Odstraní ```json ... ``` code fences z odpovědi Mistral.
    /// </summary>
    private static string StripCodeFences(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = s.IndexOf('\n');
            s = newline >= 0 ? s[(newline + 1)..] : s[3..];
        }
        if (s.EndsWith("```", StringComparison.Ordinal))
            s = s[..s.LastIndexOf("```")].TrimEnd();
        return s.Trim();
    }

    private sealed class PhotoClassificationJson
    {
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("labels")]
        public List<string>? Labels { get; set; }

        [JsonPropertyName("damage_detected")]
        public bool DamageDetected { get; set; }

        [JsonPropertyName("damage_evidence")]
        public string? DamageEvidence { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }
}
