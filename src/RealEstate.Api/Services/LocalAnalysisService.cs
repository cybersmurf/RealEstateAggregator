using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Lokální analýza nemovitosti bez cloud AI:
///   1. Načte inzerát + fotky z DB
///   2. Pro fotky bez popisu zavolá llama3.2-vision (popis v češtině)
///   3. Sestaví prompt ze šablony + popisy fotek
///   4. Vygeneruje analýzu přes chat model (výchozí qwen2.5:14b, nebo override parametrem)
///   5. Uloží do listing_analyses + vrátí výsledek
///   6. DOCX export přes pandoc
/// </summary>
public sealed class LocalAnalysisService(
    RealEstateDbContext db,
    IEmbeddingService embedding,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<LocalAnalysisService> logger) : ILocalAnalysisService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private string OllamaBaseUrl        => (config["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
    private string VisionModel          => config["Ollama:VisionModel"] ?? "llama3.2-vision:11b";
    private string? OpenRouterApiKey    => config["OpenRouter:ApiKey"];
    private string OpenRouterBaseUrl    => (config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1").TrimEnd('/');
    private string? GroqApiKey          => config["Groq:ApiKey"];
    private const string GroqBaseUrl    = "https://api.groq.com/openai/v1";
    private string? MistralApiKey       => config["Mistral:ApiKey"];
    private const string MistralBaseUrl = "https://api.mistral.ai/v1";
    private string? OllamaCloudApiKey   => config["OllamaCloud:ApiKey"];
    private string OllamaCloudBaseUrl   => (config["OllamaCloud:BaseUrl"] ?? "https://ollama.com/v1").TrimEnd('/');
    private string? AnthropicApiKey     => config["Anthropic:ApiKey"];
    private const string AnthropicBaseUrl = "https://api.anthropic.com/v1";

    // ─── Prompt pro popis fotky (česky, stručně) ─────────────────────────────
    private const string PhotoDescPrompt = """
        Popiš tuto fotografii nemovitosti stručně v češtině (2–3 věty).
        Zaměř se na: typ místnosti, viditelný stav, materiály, opotřebení, barvy, skryté vady.
        Pokud vidíš vlhkost, plísně, praskliny nebo jiné závady, zdůrazni je.
        Odpověz pouze popisem, bez úvodu.
        """;

    // ─── Sdílené OpenAI-compatible chat volání (OpenRouter, Groq, ...) ──────────
    private async Task<string> ExternalOpenAiChatAsync(
        string baseUrl, string apiKey, string model,
        string systemPrompt, string userMessage, CancellationToken ct,
        Dictionary<string, string>? extraHeaders = null)
    {
        var request = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userMessage   },
            },
            temperature = 0.3,
            max_tokens  = 4096,
        };

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        if (extraHeaders is not null)
            foreach (var (k, v) in extraHeaders)
                http.DefaultRequestHeaders.Add(k, v);

        using var body = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var resp = await http.PostAsync($"{baseUrl}/chat/completions", body, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError("ExternalChat HTTP {Status} [{BaseUrl}] model {Model}: {Body}",
                (int)resp.StatusCode, baseUrl, model, errBody[..Math.Min(500, errBody.Length)]);
            throw new HttpRequestException($"ExternalChat {(int)resp.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim() ?? string.Empty;
    }

    // ─── Ollama chat volání – lokální nebo cloud (Ollama /api/chat formát) ──────
    private async Task<string> OllamaChatDirectAsync(
        string model, string systemPrompt, string userMessage, CancellationToken ct,
        string? baseUrlOverride = null, string? apiKeyOverride = null)
    {
        var request = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = userMessage   },
            },
            stream  = false,
            options = new { temperature = 0.3, num_predict = 4096 },
        };

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrWhiteSpace(apiKeyOverride))
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKeyOverride}");

        using var body = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        var effectiveBase = (baseUrlOverride ?? OllamaBaseUrl).TrimEnd('/');
        using var resp = await http.PostAsync($"{effectiveBase}/api/chat", body, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<OllamaChatResp>(json, JsonOpts);
        return parsed?.Message?.Content?.Trim() ?? string.Empty;
    }

    // ─── Anthropic (Claude) chat volání – vlastní formát API ──────────────────
    private async Task<string> AnthropicChatAsync(
        string model, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var request = new
        {
            model,
            max_tokens = 4096,
            system   = systemPrompt,
            messages = new[] { new { role = "user", content = userMessage } },
        };

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Add("x-api-key", AnthropicApiKey
            ?? throw new InvalidOperationException("Anthropic:ApiKey není nastaven."));
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        using var body = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var resp = await http.PostAsync($"{AnthropicBaseUrl}/messages", body, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            logger.LogError("AnthropicChat HTTP {Status} model {Model}: {Body}",
                (int)resp.StatusCode, model, errBody);
            resp.EnsureSuccessStatusCode();
        }

        var json      = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?.Trim() ?? string.Empty;
    }

    // ─── Hlavní analýza ──────────────────────────────────────────────────────
    public async Task<LocalAnalysisResultDto> AnalyzeAsync(Guid listingId, string? chatModel = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Načti inzerát
        var listing = await db.Listings
            .Include(l => l.Photos.OrderBy(p => p.Order))
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        // 2. Popisy fotek
        var photoDescriptions = await DescribePhotosAsync(listing.Photos.ToList(), ct);
        logger.LogInformation("Popsáno {Count}/{Total} fotek pro {ListingId}",
            photoDescriptions.Count, listing.Photos.Count, listingId);

        // 3. Sestav systémový prompt ze šablony
        var systemPrompt = BuildSystemPrompt(listing);

        // 4. Sestav user zprávu (popis + footy)
        var userMessage = BuildUserMessage(listing, photoDescriptions);

        // 5. Vygeneruj analýzu
        var effectiveModel = chatModel?.Trim();
        logger.LogInformation("Generuji analýzu pro {ListingId} pomocí modelu {Model}...",
            listingId, effectiveModel ?? "výchozí (config)");

        var analysisMarkdown = effectiveModel is not null
            ? effectiveModel.StartsWith("groq/", StringComparison.OrdinalIgnoreCase)
                ? await ExternalOpenAiChatAsync(
                    GroqBaseUrl,
                    GroqApiKey ?? throw new InvalidOperationException("Groq:ApiKey není nastaven."),
                    effectiveModel["groq/".Length..],
                    systemPrompt, userMessage, ct)
                : effectiveModel.StartsWith("mistral/", StringComparison.OrdinalIgnoreCase)
                    ? await ExternalOpenAiChatAsync(
                        MistralBaseUrl,
                        MistralApiKey ?? throw new InvalidOperationException("Mistral:ApiKey není nastaven."),
                        effectiveModel["mistral/".Length..],
                        systemPrompt, userMessage, ct)
                : effectiveModel.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase)
                    ? await ExternalOpenAiChatAsync(
                        OpenRouterBaseUrl,
                        OpenRouterApiKey ?? throw new InvalidOperationException("OpenRouter:ApiKey není nastaven."),
                        effectiveModel["openrouter/".Length..],
                        systemPrompt, userMessage, ct,
                        new() { ["HTTP-Referer"] = "http://localhost:5001", ["X-Title"] = "RealEstateAggregator" })
                    : effectiveModel.StartsWith("ollama-cloud/", StringComparison.OrdinalIgnoreCase)
                        ? await ExternalOpenAiChatAsync(
                            OllamaCloudBaseUrl,
                            OllamaCloudApiKey ?? throw new InvalidOperationException("OllamaCloud:ApiKey není nastaven."),
                            effectiveModel["ollama-cloud/".Length..],
                            systemPrompt, userMessage, ct)
                        : effectiveModel.StartsWith("claude/", StringComparison.OrdinalIgnoreCase)
                            ? await AnthropicChatAsync(
                                effectiveModel["claude/".Length..],
                                systemPrompt, userMessage, ct)
                            : await OllamaChatDirectAsync(effectiveModel, systemPrompt, userMessage, ct)
            : await embedding.ChatAsync(systemPrompt, userMessage, ct);

        // 6. Ulož do DB – source odráží použitý model
        var sourceTag = effectiveModel is not null
            ? $"local:{effectiveModel}"
            : "qwen-local";
        var title = $"Lokální analýza [{sourceTag}] – {listing.LocationText} ({DateTime.Now:dd.MM.yyyy})";
        var analysis = new ListingAnalysis
        {
            Id             = Guid.NewGuid(),
            ListingId      = listingId,
            Title          = title,
            Content        = analysisMarkdown,
            Source         = sourceTag,
            CreatedAt      = DateTime.UtcNow,
            UpdatedAt      = DateTime.UtcNow,
        };

        // Uloži přes trackovaný context (ne AsNoTracking)
        db.ListingAnalyses.Add(analysis);
        await db.SaveChangesAsync(ct);

        sw.Stop();
        analysis.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Analýza {AnalysisId} hotova za {Elapsed:F1}s", analysis.Id, sw.Elapsed.TotalSeconds);

        return new LocalAnalysisResultDto(
            AnalysisId:      analysis.Id,
            ListingId:       listingId,
            Title:           listing.Title,
            PhotosDescribed: photoDescriptions.Count,
            PhotosTotal:     listing.Photos.Count,
            ElapsedSeconds:  sw.Elapsed.TotalSeconds,
            MarkdownContent: analysisMarkdown
        );
    }

    // ─── DOCX export ─────────────────────────────────────────────────────────
    public async Task<byte[]?> ExportDocxAsync(Guid listingId, string? chatModel = null, CancellationToken ct = default)
    {
        // Najdi poslední analýzu odpovídající modelu
        var sourceTag = chatModel is not null ? $"local:{chatModel.Trim()}" : "qwen-local";
        var analysis = await db.ListingAnalyses
            .AsNoTracking()
            .Where(a => a.ListingId == listingId && a.Source == sourceTag)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (analysis is null)
        {
            // Spusť novou analýzu
            var result = await AnalyzeAsync(listingId, chatModel, ct);
            analysis = new ListingAnalysis { Content = result.MarkdownContent, Title = result.Title };
        }

        return await ConvertMarkdownToDocxAsync(analysis.Content, analysis.Title ?? "Analýza", ct);
    }

    // ─── Popisy fotek ────────────────────────────────────────────────────────
    private async Task<List<(int Order, string Category, string Description)>> DescribePhotosAsync(
        List<ListingPhoto> photos, CancellationToken ct)
    {
        var result = new List<(int Order, string Category, string Description)>();

        // Přednostně použij existující popisy
        var withDesc = photos
            .Where(p => !string.IsNullOrWhiteSpace(p.PhotoDescription))
            .OrderBy(p => p.Order)
            .ToList();

        foreach (var p in withDesc)
        {
            result.Add((p.Order, p.PhotoCategory ?? "foto", p.PhotoDescription!));
        }

        // Pro fotky bez popisu: zavolej vision model (všechny, žádný limit)
        var withoutDesc = photos
            .Where(p => string.IsNullOrWhiteSpace(p.PhotoDescription) && !string.IsNullOrWhiteSpace(p.OriginalUrl))
            .OrderBy(p => p.Order)
            .ToList();

        if (withoutDesc.Count > 0)
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            foreach (var photo in withoutDesc)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var desc = await DescribePhotoViaVisionAsync(http, photo.OriginalUrl!, photo.Id, ct);
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        result.Add((photo.Order, photo.PhotoCategory ?? "foto", desc));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Vision popis selhal pro foto {Id}", photo.Id);
                }
            }
        }

        return result.OrderBy(r => r.Order).ToList();
    }

    private async Task<string?> DescribePhotoViaVisionAsync(
        HttpClient http, string imageUrl, Guid photoId, CancellationToken ct)
    {
        // Stáhni obrázek
        byte[] imageBytes;
        try
        {
            imageBytes = await http.GetByteArrayAsync(imageUrl, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Nelze stáhnout fotku {Url}", imageUrl);
            return null;
        }

        var base64 = Convert.ToBase64String(imageBytes);

        var request = new
        {
            model   = VisionModel,
            prompt  = PhotoDescPrompt,
            images  = new[] { base64 },
            stream  = false,
            options = new { temperature = 0.2, num_predict = 150 }
        };

        using var ollamaHttp = httpClientFactory.CreateClient("OllamaVision");
        using var content = new StringContent(
            JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        using var response = await ollamaHttp.PostAsync($"{OllamaBaseUrl}/api/generate", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Vision HTTP {Status} pro foto {Id}", (int)response.StatusCode, photoId);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<OllamaGenerateResp>(body, JsonOpts);
        return parsed?.Response?.Trim();
    }

    // ─── Prompt building ─────────────────────────────────────────────────────
    private string BuildSystemPrompt(Listing listing)
    {
        var isNewBuild = IsNewBuild(listing);
        var templateFile = isNewBuild ? "ai_instrukce_newbuild.md" : "ai_instrukce_existing.md";
        var templatePath = Path.Combine(env.ContentRootPath, "Templates", templateFile);

        string template;
        if (File.Exists(templatePath))
            template = File.ReadAllText(templatePath, Encoding.UTF8);
        else
        {
            logger.LogWarning("Šablona nenalezena: {Path}", templatePath);
            template = "Proveď komplexní analýzu nemovitosti. Odpovídej česky, strukturovaně.";
        }

        // Doplň placeholders
        var price = listing.Price.HasValue
            ? $"{listing.Price.Value:N0} Kč".Replace(",", " ")
            : "neuvedena";

        var replacements = new Dictionary<string, string>
        {
            ["{{LOCATION}}"]           = listing.LocationText,
            ["{{PROPERTY_TYPE}}"]      = listing.PropertyType.ToString(),
            ["{{OFFER_TYPE}}"]         = listing.OfferType.ToString(),
            ["{{PRICE}}"]              = price,
            ["{{PRICE_NOTE}}"]         = listing.PriceNote is not null ? $" ({listing.PriceNote})" : "",
            ["{{AREA}}"]               = listing.AreaBuiltUp.HasValue ? $"{listing.AreaBuiltUp:F0} m²" : "neuvedena",
            ["{{ROOMS_LINE}}"]         = listing.Disposition is not null ? $"**Dispozice:** {listing.Disposition}\n" : "",
            ["{{CONSTRUCTION_TYPE_LINE}}"] = listing.ConstructionType is not null ? $"**Typ stavby:** {listing.ConstructionType}\n" : "",
            ["{{CONDITION_LINE}}"]     = listing.Condition is not null ? $"**Stav:** {listing.Condition}\n" : "",
            ["{{SOURCE_NAME}}"]        = listing.SourceName,
            ["{{SOURCE_CODE}}"]        = listing.SourceCode,
            ["{{URL}}"]                = listing.Url,
            ["{{PHOTO_LINKS_SECTION}}"]= "",
            ["{{DRIVE_FOLDER_SECTION}}"]= "",
        };

        foreach (var (key, val) in replacements)
            template = template.Replace(key, val);

        return template;
    }

    private static string BuildUserMessage(
        Listing listing,
        List<(int Order, string Category, string Description)> photoDescs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## POPIS NEMOVITOSTI Z INZERÁTU");
        sb.AppendLine();
        sb.AppendLine(listing.Description ?? "_Bez popisu._");
        sb.AppendLine();

        if (photoDescs.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## POPISY FOTOGRAFIÍ (zpracováno AI)");
            sb.AppendLine();
            sb.AppendLine($"Celkem {photoDescs.Count} fotek bylo analyzováno:");
            sb.AppendLine();

            foreach (var (order, category, desc) in photoDescs)
            {
                sb.AppendLine($"**Foto {order + 1}** [{category}]: {desc}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Nyní proveď **kompletní analýzu** dle instrukcí výše. Piš v češtině, strukturovaně, využij informace z fotek.");

        return sb.ToString();
    }

    // ─── DOCX via pandoc ─────────────────────────────────────────────────────
    private async Task<byte[]?> ConvertMarkdownToDocxAsync(
        string markdownContent, string title, CancellationToken ct)
    {
        var tmpDir    = Path.GetTempPath();
        var mdFile    = Path.Combine(tmpDir, $"analysis_{Guid.NewGuid():N}.md");
        var docxFile  = Path.Combine(tmpDir, $"analysis_{Guid.NewGuid():N}.docx");

        try
        {
            // Přidej YAML frontmatter pro lepší DOCX formátování
            var mdWithMeta = $"""
                ---
                title: "{title.Replace("\"", "'")}"
                lang: cs
                ---

                {markdownContent}
                """;

            await File.WriteAllTextAsync(mdFile, mdWithMeta, Encoding.UTF8, ct);

            // Najdi pandoc
            var pandocPath = await FindPandocAsync();
            if (pandocPath is null)
            {
                logger.LogError("pandoc nenalezen. Instaluj: brew install pandoc");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName               = pandocPath,
                Arguments              = $"\"{mdFile}\" -o \"{docxFile}\" --from=markdown --to=docx -s",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Nepodařilo se spustit pandoc");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var err = await process.StandardError.ReadToEndAsync(ct);
                logger.LogError("pandoc selhalo (exit {Code}): {Err}", process.ExitCode, err);
                return null;
            }

            if (!File.Exists(docxFile))
            {
                logger.LogError("pandoc nevytvořilo výstupní soubor: {Path}", docxFile);
                return null;
            }

            return await File.ReadAllBytesAsync(docxFile, ct);
        }
        finally
        {
            if (File.Exists(mdFile))  File.Delete(mdFile);
            if (File.Exists(docxFile)) File.Delete(docxFile);
        }
    }

    private static async Task<string?> FindPandocAsync()
    {
        foreach (var candidate in new[] { "/usr/bin/pandoc", "/opt/homebrew/bin/pandoc", "/usr/local/bin/pandoc", "pandoc" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = candidate,
                    Arguments              = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                if (p is not null)
                {
                    await p.WaitForExitAsync();
                    if (p.ExitCode == 0) return candidate;
                }
            }
            catch { /* zkus další */ }
        }
        return null;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────
    private static bool IsNewBuild(Listing l)
    {
        var text = $"{l.Title} {l.Description} {l.Condition}".ToLowerInvariant();
        return text.Contains("novostavb")
            || text.Contains("ve výstavb")
            || text.Contains("pod klíč")
            || text.Contains("developerský projekt")
            || l.Condition is "Nový" or "Nová";
    }

    // Interní DTO pro Ollama /api/generate response
    private sealed record OllamaGenerateResp(string? Response);

    // Interní DTO pro Ollama /api/chat response
    private sealed record OllamaChatResp(OllamaChatMessage? Message);
    private sealed record OllamaChatMessage(string? Role, string? Content);
}
