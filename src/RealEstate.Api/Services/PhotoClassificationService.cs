using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Klasifikuje fotky nemovitostí přes Ollama Vision API (llama3.2-vision).
/// Čte soubory z lokálního storage (wwwroot/uploads/...) a posílá jako base64.
/// Výsledky ukládá do sloupců photo_category, photo_labels, damage_detected, classified_at.
/// </summary>
public sealed class PhotoClassificationService(
    RealEstateDbContext db,
    IWebHostEnvironment env,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PhotoClassificationService> logger) : IPhotoClassificationService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Ollama generate endpoint
    private string OllamaBaseUrl =>
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
        ?? configuration["Ollama:BaseUrl"]
        ?? "http://localhost:11434";

    private string VisionModel =>
        Environment.GetEnvironmentVariable("OLLAMA_VISION_MODEL")
        ?? configuration["Ollama:VisionModel"]
        ?? "llama3.2-vision:11b";

    // Public base URL odstraníme ze stored_url abychom dostali relativní cestu k souboru
    private string PublicBaseUrl =>
        Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
        ?? configuration["Photos:PublicBaseUrl"]
        ?? "http://localhost:5001";

    // ── 1. Prompt: strukturovaný JSON klasifikace (category + labels + damage) ──
    // format: "json" zaručuje valid JSON výstup, ale omezuje délku → neklást dlouhé texty
    private const string ClassificationPrompt = """
        Analyze this real estate property photo.
        Respond ONLY with valid JSON, nothing else:
        {"category":"...","labels":[...],"damage_detected":false,"confidence":0.9}

        "category" must be exactly one of:
        exterior, interior, kitchen, bathroom, living_room, bedroom,
        attic, basement, garage, land, floor_plan, damage, other

        "labels": array of 0-5 tags from:
        mold, water_damage, crack, broken_windows, damaged_roof, renovation_needed,
        garden, pool, fireplace, wooden_beams, new_construction, renovated,
        brick_walls, wooden_construction, panel_building

        "damage_detected": true if ANY visible damage (mold, water stains, cracks, rot, peeling)
        "confidence": 0.0 to 1.0
        """;

    // ── 2. Prompt: volný text popis česky (bez format:json, jinak se seká) ─────
    // Jednoduchý anglický prompt – model pracuje lépe v angličtině,
    // výsledek uložen tak jak přijde (EN), v UI bude přeložen nebo zobrazen i anglicky
    private const string DescriptionPrompt =
        "Describe what you see in this real estate property photo in 1-2 sentences." +
        " Focus on materials, condition, size impression, and any notable features or defects." +
        " Be specific and concise.";

    public async Task<PhotoClassificationResultDto> ClassifyBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        // Fotky stažené lokálně NEBO s original_url – stačí mít odkud načíst obrázek
        var photos = await db.ListingPhotos
            .Where(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null)
            .Where(p => listingId == null || p.ListingId == listingId)
            .OrderBy(p => p.ListingId)
            .ThenBy(p => p.Order)
            .Take(batchSize)
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remaining0 = await db.ListingPhotos
                .CountAsync(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null
                    && (listingId == null || p.ListingId == listingId), ct);
            return new PhotoClassificationResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var httpClient = httpClientFactory.CreateClient("OllamaVision");

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
                    using var dlClient = httpClientFactory.CreateClient();
                    dlClient.Timeout = TimeSpan.FromSeconds(30);
                    try
                    {
                        imageBytes = await dlClient.GetByteArrayAsync(photo.OriginalUrl, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            "Failed to fetch original_url for listing {ListingId} photo {Order}: {Ex}",
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
                var base64 = Convert.ToBase64String(imageBytes);

                var ollamaUrl = $"{OllamaBaseUrl.TrimEnd('/')}/api/generate";

                // ── 1. Volání: JSON klasifikace (format:json zaručuje valid JSON) ─────
                var classifyRequest = new
                {
                    model = VisionModel,
                    prompt = ClassificationPrompt,
                    images = new[] { base64 },
                    stream = false,
                    format = "json",
                    options = new { temperature = 0.1, num_predict = 256 }
                };

                using var classifyContent = new StringContent(
                    JsonSerializer.Serialize(classifyRequest), Encoding.UTF8, "application/json");
                using var classifyResponse = await httpClient.PostAsync(ollamaUrl, classifyContent, ct);

                if (!classifyResponse.IsSuccessStatusCode)
                {
                    var body = await classifyResponse.Content.ReadAsStringAsync(ct);
                    logger.LogWarning(
                        "Ollama classify HTTP {Status} for listing {ListingId} photo {Order}: {Body}",
                        (int)classifyResponse.StatusCode, photo.ListingId, photo.Order,
                        body[..Math.Min(200, body.Length)]);
                    failed++;
                    continue;
                }

                var classifyBody = await classifyResponse.Content.ReadAsStringAsync(ct);
                var classifyOllama = JsonSerializer.Deserialize<OllamaGenerateResponse>(classifyBody, _jsonOptions);

                if (string.IsNullOrWhiteSpace(classifyOllama?.Response))
                {
                    logger.LogWarning("Empty JSON response for listing {ListingId} photo {Order}",
                        photo.ListingId, photo.Order);
                    failed++;
                    continue;
                }

                // Parsuj JSON klasifikaci (regex fallback pokud JSON useknutý)
                PhotoClassificationJson? classification = null;
                try
                {
                    classification = JsonSerializer.Deserialize<PhotoClassificationJson>(
                        classifyOllama.Response, _jsonOptions);
                }
                catch (JsonException)
                {
                    classification = TryParsePartialJson(classifyOllama.Response);
                }

                if (classification == null || string.IsNullOrWhiteSpace(classification.Category))
                {
                    logger.LogWarning(
                        "Could not parse classification JSON for listing {ListingId} photo {Order}: {Raw}",
                        photo.ListingId, photo.Order,
                        classifyOllama.Response[..Math.Min(200, classifyOllama.Response.Length)]);
                    failed++;
                    continue;
                }

                // ── 2. Volání: volný text popis česky (BEZ format:json – neskne se) ──
                string? photoDescription = null;
                try
                {
                    var descRequest = new
                    {
                        model = VisionModel,
                        prompt = DescriptionPrompt,
                        images = new[] { base64 },
                        stream = false,
                        options = new { temperature = 0.3, num_predict = 200 }
                        // Záměrně BEZ format:json – volný text je spolehlivější pro delší výstup
                    };

                    using var descContent = new StringContent(
                        JsonSerializer.Serialize(descRequest), Encoding.UTF8, "application/json");
                    using var descResponse = await httpClient.PostAsync(ollamaUrl, descContent, ct);

                    if (descResponse.IsSuccessStatusCode)
                    {
                        var descBody = await descResponse.Content.ReadAsStringAsync(ct);
                        var descOllama = JsonSerializer.Deserialize<OllamaGenerateResponse>(descBody, _jsonOptions);
                        if (!string.IsNullOrWhiteSpace(descOllama?.Response))
                        {
                            var raw = descOllama.Response.Trim();
                            // Ořízni na max 400 znaků na celé větě (LLM někdy opakuje věty)
                            photoDescription = TrimToSentence(raw, maxLength: 400);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Popis není kritický – logujeme jako debug, pokračujeme bez něj
                    logger.LogDebug(ex,
                        "Description call failed for listing {ListingId} photo {Order}, continuing without it",
                        photo.ListingId, photo.Order);
                }

                // ── Uložení výsledku do DB ──────────────────────────────────────────
                photo.PhotoCategory = NormalizeCategory(classification.Category);
                photo.PhotoDescription = photoDescription;
                photo.PhotoLabels = classification.Labels?.Count > 0
                    ? JsonSerializer.Serialize(classification.Labels)
                    : null;
                photo.DamageDetected = classification.DamageDetected;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Classification failed for listing {ListingId} photo {Order}",
                    photo.ListingId, photo.Order);
                failed++;
            }
        }

        if (succeeded > 0)
            await db.SaveChangesAsync(ct);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;
        var remaining = await db.ListingPhotos
            .CountAsync(p => (p.StoredUrl != null || p.OriginalUrl != null) && p.ClassifiedAt == null
                && (listingId == null || p.ListingId == listingId), ct);

        logger.LogInformation(
            "Photo classification batch: {Processed} processed, {Succeeded} OK, {Failed} failed. Remaining: {Remaining}. Avg: {Avg}ms",
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));

        return new PhotoClassificationResultDto(
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));
    }

    public async Task<PhotoClassificationResultDto> ClassifyInspectionBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        var photos = await db.UserListingPhotos
            .Where(p => p.ClassifiedAt == null)
            .Where(p => listingId == null || p.ListingId == listingId)
            .OrderBy(p => p.ListingId)
            .ThenBy(p => p.UploadedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remaining0 = await db.UserListingPhotos
                .CountAsync(p => p.ClassifiedAt == null
                    && (listingId == null || p.ListingId == listingId), ct);
            return new PhotoClassificationResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var httpClient = httpClientFactory.CreateClient("OllamaVision");

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
                var (classification, description) = await RunOllamaClassificationAsync(
                    httpClient, imageBytes, photo.ListingId, photo.Id, ct);

                if (classification == null)
                {
                    failed++;
                    continue;
                }

                photo.PhotoCategory          = NormalizeCategory(classification.Category!);
                photo.PhotoLabels            = classification.Labels?.Count > 0
                    ? JsonSerializer.Serialize(classification.Labels) : null;
                photo.DamageDetected         = classification.DamageDetected;
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Classification failed for inspection photo {PhotoId} listing {ListingId}",
                    photo.Id, photo.ListingId);
                failed++;
            }
        }

        if (succeeded > 0)
            await db.SaveChangesAsync(ct);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;
        var remaining = await db.UserListingPhotos
            .CountAsync(p => p.ClassifiedAt == null
                && (listingId == null || p.ListingId == listingId), ct);

        logger.LogInformation(
            "Inspection photo classification: {Processed} processed, {Succeeded} OK, {Failed} failed. Remaining: {Remaining}. Avg: {Avg}ms",
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));

        return new PhotoClassificationResultDto(
            photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));
    }

    /// <summary>
    /// Sdílená Ollama logika pro oba typy fotek (listing + inspection).
    /// Vrátí (classification, description) nebo (null, null) při selhání.
    /// </summary>
    private async Task<(PhotoClassificationJson? Classification, string? Description)> RunOllamaClassificationAsync(
        HttpClient httpClient, byte[] imageBytes, Guid listingId, Guid photoId, CancellationToken ct)
    {
        var base64    = Convert.ToBase64String(imageBytes);
        var ollamaUrl = $"{OllamaBaseUrl.TrimEnd('/')}/api/generate";

        // 1. JSON klasifikace
        var classifyRequest = new
        {
            model   = VisionModel,
            prompt  = ClassificationPrompt,
            images  = new[] { base64 },
            stream  = false,
            format  = "json",
            options = new { temperature = 0.1, num_predict = 256 }
        };

        using var classifyContent = new StringContent(
            JsonSerializer.Serialize(classifyRequest), Encoding.UTF8, "application/json");
        using var classifyResponse = await httpClient.PostAsync(ollamaUrl, classifyContent, ct);

        if (!classifyResponse.IsSuccessStatusCode)
        {
            var body = await classifyResponse.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Ollama classify HTTP {Status} for {ListingId}/{PhotoId}: {Body}",
                (int)classifyResponse.StatusCode, listingId, photoId,
                body[..Math.Min(200, body.Length)]);
            return (null, null);
        }

        var classifyBody   = await classifyResponse.Content.ReadAsStringAsync(ct);
        var classifyOllama = JsonSerializer.Deserialize<OllamaGenerateResponse>(classifyBody, _jsonOptions);

        if (string.IsNullOrWhiteSpace(classifyOllama?.Response))
        {
            logger.LogWarning("Empty JSON response for {ListingId}/{PhotoId}", listingId, photoId);
            return (null, null);
        }

        PhotoClassificationJson? classification = null;
        try { classification = JsonSerializer.Deserialize<PhotoClassificationJson>(classifyOllama.Response, _jsonOptions); }
        catch (JsonException) { classification = TryParsePartialJson(classifyOllama.Response); }

        if (classification == null || string.IsNullOrWhiteSpace(classification.Category))
        {
            logger.LogWarning("Could not parse classification for {ListingId}/{PhotoId}: {Raw}",
                listingId, photoId,
                classifyOllama.Response[..Math.Min(200, classifyOllama.Response.Length)]);
            return (null, null);
        }

        // 2. Volný text popis
        string? description = null;
        try
        {
            var descRequest = new
            {
                model   = VisionModel,
                prompt  = DescriptionPrompt,
                images  = new[] { base64 },
                stream  = false,
                options = new { temperature = 0.3, num_predict = 200 }
            };
            using var descContent = new StringContent(
                JsonSerializer.Serialize(descRequest), Encoding.UTF8, "application/json");
            using var descResponse = await httpClient.PostAsync(ollamaUrl, descContent, ct);
            if (descResponse.IsSuccessStatusCode)
            {
                var descBody   = await descResponse.Content.ReadAsStringAsync(ct);
                var descOllama = JsonSerializer.Deserialize<OllamaGenerateResponse>(descBody, _jsonOptions);
                if (!string.IsNullOrWhiteSpace(descOllama?.Response))
                    description = TrimToSentence(descOllama.Response.Trim(), maxLength: 400);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Description call failed for {ListingId}/{PhotoId}, skipping", listingId, photoId);
        }

        return (classification, description);
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

        return new PhotoClassificationJson
        {
            Category = categoryMatch.Groups[1].Value,
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

    // ── Interní deserialization modely ───────────────────────────────────────

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }
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

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }
}
