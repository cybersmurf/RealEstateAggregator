using System.Diagnostics;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
///   6. DOCX export přes DocumentFormat.OpenXml (pure .NET, žádné ext. závislosti)
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

    // ─── Groq Tool-Use loop (OpenAI-compatible function calling) ─────────────
    // Prefix: groq-tools/<model>  př. groq-tools/llama-3.3-70b-versatile
    // Tok: 1) Groq dostane nástroje  2) Volá je → .NET vykoná  3) Groq vygeneruje analýzu
    private async Task<string> GroqToolingAsync(
        string model, Guid listingId, Listing listing, string? inspectionNotes,
        string systemPrompt, CancellationToken ct)
    {
        var apiKey = GroqApiKey ?? throw new InvalidOperationException("Groq:ApiKey není nastaven.");

        // ── Definice nástrojů (OpenAI function calling formát) ───────────────
        var toolDefs = new object[]
        {
            new {
                type = "function",
                function = new {
                    name = "get_listing_details",
                    description = "Načte kompletní detail inzerátu: cena, plocha, lokace, dispozice, stav, typ stavby, rok výstavby, popis z inzerátu, URL, zdroj.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "get_photo_descriptions",
                    description = "Načte AI klasifikace a popisy fotografií nemovitosti (exteriér, interiér, koupelna, zahrada…). Klíčové pro posouzení fyzického stavu.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "get_cadastre_data",
                    description = "Načte data z katastru nemovitostí: parcelní číslo, LV, výměra, druh pozemku, zástavní práva a věcná břemena.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
        };

        // ── Počáteční zprávy ─────────────────────────────────────────────────
        var userPrompt   = BuildToolUserPrompt(listing, inspectionNotes);

        var messagesList = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userPrompt },
        };

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        // ── Tool-use loop (max 8 iterací) ────────────────────────────────────
        for (int turn = 0; turn < 8; turn++)
        {
            var requestObj = new
            {
                model,
                messages = messagesList,
                tools = toolDefs,
                tool_choice = turn == 0 ? (object)"required" : "auto",  // první turn MUSÍ zavolat tool
                temperature = 0.3,
                max_tokens  = 4096,
            };

            using var body = new StringContent(
                JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{GroqBaseUrl}/chat/completions", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                logger.LogError("GroqTools HTTP {Status} turn {Turn}: {Err}", (int)resp.StatusCode, turn, err[..Math.Min(400, err.Length)]);
                throw new HttpRequestException($"GroqTools HTTP {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var choice       = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.GetProperty("finish_reason").GetString();
            var assistantMsg = choice.GetProperty("message");

            // Přidej assistant zprávu do historie jako JsonElement (zachová tool_calls strukturu)
            messagesList.Add(assistantMsg.Clone());

            logger.LogInformation("GroqTools turn {Turn}/{Max}: finish_reason={Reason}", turn + 1, 8, finishReason);

            if (finishReason is "stop" or "length")
            {
                // Finální odpověď
                var content = assistantMsg.TryGetProperty("content", out var c) ? c.GetString() : null;
                return content?.Trim() ?? string.Empty;
            }

            if (finishReason == "tool_calls" &&
                assistantMsg.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var toolCallId = toolCall.GetProperty("id").GetString()!;
                    var funcName   = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                    var argsJson   = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";

                    using var argsDoc  = JsonDocument.Parse(argsJson);
                    var toolResult = await ExecuteGroqToolAsync(funcName, argsDoc.RootElement, listingId, ct);

                    logger.LogInformation("GroqTools tool={Tool} → {Len} znaků výsledku", funcName, toolResult.Length);

                    messagesList.Add(new { role = "tool", tool_call_id = toolCallId, content = toolResult });
                }
                continue;
            }

            // Neočekávaný finish_reason
            logger.LogWarning("GroqTools: neočekávaný finish_reason={Reason}, ukončuji loop", finishReason);
            break;
        }

        // Fallback: pokud loopoval až do konce, vyžádáme finální odpověď bez tools
        logger.LogWarning("GroqTools: dosažen limit turns, generuji bez tools");
        messagesList.Add(new { role = "user", content = "Na základě informací výše napiš závěrečnou strukturovanou analýzu v češtině. Nepoužívej další nástroje." });
        var fbReq = new { model, messages = messagesList, temperature = 0.3, max_tokens = 4096 };
        using var fbBody = new StringContent(JsonSerializer.Serialize(fbReq), Encoding.UTF8, "application/json");
        using var fbHttp = httpClientFactory.CreateClient();
        fbHttp.Timeout = TimeSpan.FromMinutes(3);
        fbHttp.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        using var fbResp = await fbHttp.PostAsync($"{GroqBaseUrl}/chat/completions", fbBody, ct);
        var fbJson = await fbResp.Content.ReadAsStringAsync(ct);
        using var fbDoc = JsonDocument.Parse(fbJson);
        return fbDoc.RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
            .GetString()?.Trim() ?? string.Empty;
    }

    // ─── Mistral Tool-Use loop (OpenAI-compatible function calling) ──────────
    // Prefix: mistral-tools/<model>  př. mistral-tools/mistral-large-latest
    // Mistral používá tool_choice="any" (Groq používá "required") – jinak identické s GroqToolingAsync
    private async Task<string> MistralToolingAsync(
        string model, Guid listingId, Listing listing, string? inspectionNotes,
        string systemPrompt, CancellationToken ct)
    {
        var apiKey = MistralApiKey ?? throw new InvalidOperationException("Mistral:ApiKey není nastaven.");

        var toolDefs = new object[]
        {
            new {
                type = "function",
                function = new {
                    name = "get_listing_details",
                    description = "Načte kompletní detail inzerátu: cena, plocha, lokace, dispozice, stav, typ stavby, rok výstavby, popis z inzerátu, URL, zdroj.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "get_photo_descriptions",
                    description = "Načte AI klasifikace a popisy fotografií nemovitosti (exteriér, interiér, koupelna, zahrada…). Klíčové pro posouzení fyzického stavu.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
            new {
                type = "function",
                function = new {
                    name = "get_cadastre_data",
                    description = "Načte data z katastru nemovitostí: parcelní číslo, LV, výměra, druh pozemku, zástavní práva a věcná břemena.",
                    parameters = new {
                        type = "object",
                        properties = new { listing_id = new { type = "string", description = "UUID inzerátu" } },
                        required = new[] { "listing_id" }
                    }
                }
            },
        };

        var userPrompt   = BuildToolUserPrompt(listing, inspectionNotes);
        var messagesList = new List<object>
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = userPrompt },
        };

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        for (int turn = 0; turn < 8; turn++)
        {
            var requestObj = new
            {
                model,
                messages    = messagesList,
                tools       = toolDefs,
                tool_choice = turn == 0 ? (object)"any" : "auto",  // Mistral: "any" = musí zavolat tool
                temperature = 0.3,
                max_tokens  = 4096,
            };

            using var body = new StringContent(
                JsonSerializer.Serialize(requestObj), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{MistralBaseUrl}/chat/completions", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                logger.LogError("MistralTools HTTP {Status} turn {Turn}: {Err}", (int)resp.StatusCode, turn, err[..Math.Min(400, err.Length)]);
                throw new HttpRequestException($"MistralTools HTTP {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var choice       = doc.RootElement.GetProperty("choices")[0];
            var finishReason = choice.GetProperty("finish_reason").GetString();
            var assistantMsg = choice.GetProperty("message");

            messagesList.Add(assistantMsg.Clone());

            logger.LogInformation("MistralTools turn {Turn}/{Max}: finish_reason={Reason}", turn + 1, 8, finishReason);

            if (finishReason is "stop" or "length")
            {
                var content = assistantMsg.TryGetProperty("content", out var c) ? c.GetString() : null;
                return content?.Trim() ?? string.Empty;
            }

            if (finishReason == "tool_calls" &&
                assistantMsg.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var toolCallId = toolCall.GetProperty("id").GetString()!;
                    var funcName   = toolCall.GetProperty("function").GetProperty("name").GetString()!;
                    var argsJson   = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";

                    using var argsDoc  = JsonDocument.Parse(argsJson);
                    // Znovupoužití ExecuteGroqToolAsync – logika je nezávislá na provideru
                    var toolResult = await ExecuteGroqToolAsync(funcName, argsDoc.RootElement, listingId, ct);

                    logger.LogInformation("MistralTools tool={Tool} → {Len} znaků výsledku", funcName, toolResult.Length);

                    messagesList.Add(new { role = "tool", tool_call_id = toolCallId, content = toolResult });
                }
                continue;
            }

            logger.LogWarning("MistralTools: neočekávaný finish_reason={Reason}, ukončuji loop", finishReason);
            break;
        }

        logger.LogWarning("MistralTools: dosažen limit turns, generuji bez tools");
        messagesList.Add(new { role = "user", content = "Na základě informací výše napiš závěrečnou strukturovanou analýzu v češtině. Nepoužívej další nástroje." });
        var fbReqM = new { model, messages = messagesList, temperature = 0.3, max_tokens = 4096 };
        using var fbBodyM  = new StringContent(JsonSerializer.Serialize(fbReqM), Encoding.UTF8, "application/json");
        using var fbHttpM  = httpClientFactory.CreateClient();
        fbHttpM.Timeout = TimeSpan.FromMinutes(5);
        fbHttpM.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        using var fbRespM  = await fbHttpM.PostAsync($"{MistralBaseUrl}/chat/completions", fbBodyM, ct);
        var fbJsonM = await fbRespM.Content.ReadAsStringAsync(ct);
        using var fbDocM = JsonDocument.Parse(fbJsonM);
        return fbDocM.RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
            .GetString()?.Trim() ?? string.Empty;
    }

    // ─── Vykonání Groq tool callu ─────────────────────────────────────────────
    private async Task<string> ExecuteGroqToolAsync(
        string funcName, JsonElement args, Guid listingId, CancellationToken ct)
    {
        switch (funcName)
        {
            case "get_listing_details":
            {
                var listing = await db.Listings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == listingId, ct);
                if (listing is null) return "Inzerát nenalezen.";

                var sb = new StringBuilder();
                sb.AppendLine($"# {listing.Title}");
                sb.AppendLine($"- **Lokace:** {listing.LocationText}");
                sb.AppendLine($"- **Cena:** {(listing.Price.HasValue ? $"{listing.Price.Value:N0} Kč" : "neuvedena")}{(listing.PriceNote is not null ? $" ({listing.PriceNote})" : "")}");
                sb.AppendLine($"- **Typ:** {listing.PropertyType} / {listing.OfferType}");
                if (listing.AreaBuiltUp.HasValue) sb.AppendLine($"- **Zastavěná plocha:** {listing.AreaBuiltUp:F0} m²");
                if (listing.AreaLand.HasValue)    sb.AppendLine($"- **Pozemek:** {listing.AreaLand:F0} m²");
                if (listing.Disposition is not null)      sb.AppendLine($"- **Dispozice:** {listing.Disposition}");
                if (listing.ConstructionType is not null) sb.AppendLine($"- **Typ stavby:** {listing.ConstructionType}");
                if (listing.Condition is not null)        sb.AppendLine($"- **Stav:** {listing.Condition}");
                // Rok výstavby je uložen v AiNormalizedData JSON (klíč "rok_stavby")
                if (listing.AiNormalizedData is not null)
                {
                    try
                    {
                        using var ndoc = JsonDocument.Parse(listing.AiNormalizedData);
                        if (ndoc.RootElement.TryGetProperty("rok_stavby", out var yrEl) &&
                            yrEl.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                            sb.AppendLine($"- **Rok výstavby:** {yrEl}");
                    }
                    catch { /* ignorovat malformed JSON */ }
                }
                sb.AppendLine($"- **GPS:** {(listing.Latitude.HasValue ? $"{listing.Latitude:F6}, {listing.Longitude:F6}" : "–")}");
                sb.AppendLine($"- **Zdroj:** {listing.SourceName} ({listing.SourceCode})");
                sb.AppendLine($"- **URL:** {listing.Url}");
                sb.AppendLine();
                sb.AppendLine("## Popis z inzerátu:");
                sb.AppendLine(listing.Description ?? "(bez popisu)");
                return sb.ToString();
            }

            case "get_photo_descriptions":
            {
                var photos = await db.ListingPhotos
                    .AsNoTracking()
                    .Where(p => p.ListingId == listingId && p.ClassifiedAt != null)
                    .OrderBy(p => p.Order)
                    .ToListAsync(ct);

                if (photos.Count == 0)
                    return "Žádné klasifikované fotografie nejsou k dispozici pro tento inzerát.";

                var sb = new StringBuilder();
                sb.AppendLine($"# Fotografie ({photos.Count} klasifikovaných):");
                foreach (var p in photos)
                {
                    sb.Append($"- **Foto {p.Order + 1}** [{p.PhotoCategory ?? "?"}]");
                    if (p.DamageDetected == true) sb.Append(" ⚠️ VIDITELNÉ POŠKOZENÍ");
                    if (!string.IsNullOrWhiteSpace(p.PhotoDescription))
                        sb.Append($": {p.PhotoDescription}");
                    var labels = p.PhotoLabels is not null
                        ? JsonSerializer.Deserialize<List<string>>(p.PhotoLabels)
                        : null;
                    if (labels?.Count > 0)
                        sb.Append($" [štítky: {string.Join(", ", labels)}]");
                    sb.AppendLine();
                }
                var dmg = photos.Count(p => p.DamageDetected == true);
                if (dmg > 0) sb.AppendLine($"\n⚠️ Celkem {dmg}/{photos.Count} fotek detekuje poškození!");
                // Limit 3000 znaků šetří Groq TPM (foto výstup byl 7646 = ~1900 tokenů)
                var result = sb.ToString();
                return result.Length > 3000 ? result[..3000] + "\n…(zkráceno)" : result;
            }

            case "get_cadastre_data":
            {
                try
                {
                    var conn = db.Database.GetDbConnection();
                    if (conn.State != System.Data.ConnectionState.Open)
                        await conn.OpenAsync(ct);

                    using var cmd = conn.CreateCommand();
                    // Sloupce: parcel_number(0), lv_number(1), land_area_m2(2), land_type(3), owner_type(4), encumbrances(5)
                    cmd.CommandText =
                        "SELECT parcel_number, lv_number, land_area_m2, land_type, owner_type, encumbrances::text " +
                        "FROM re_realestate.listing_cadastre_data " +
                        "WHERE listing_id = @id LIMIT 1";
                    var p = cmd.CreateParameter();
                    p.ParameterName = "@id"; p.Value = listingId;
                    cmd.Parameters.Add(p);

                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                        return "Data z katastru nejsou pro tento inzerát k dispozici.";

                    var sb = new StringBuilder();
                    sb.AppendLine("# Data z katastru nemovitostí:");
                    if (!reader.IsDBNull(0)) sb.AppendLine($"- **Parcelní číslo:** {reader.GetString(0)}");
                    if (!reader.IsDBNull(1)) sb.AppendLine($"- **List vlastnictví (LV):** {reader.GetString(1)}");
                    if (!reader.IsDBNull(2)) sb.AppendLine($"- **Výměra parcely:** {reader.GetInt32(2)} m²");
                    if (!reader.IsDBNull(3)) sb.AppendLine($"- **Druh pozemku:** {reader.GetString(3)}");
                    if (!reader.IsDBNull(4)) sb.AppendLine($"- **Vlastník (typ):** {reader.GetString(4)}");
                    if (!reader.IsDBNull(5) && !string.IsNullOrWhiteSpace(reader.GetString(5)) && reader.GetString(5) != "[]")
                        sb.AppendLine($"- **Věcná břemena / zástavní práva:** {reader.GetString(5)}");
                    return sb.ToString();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "get_cadastre_data: tabulka nebo data nedostupná pro {ListingId}", listingId);
                    return "Data z katastru nemovitostí nejsou k dispozici.";
                }
            }

            default:
                return $"Nástroj '{funcName}' není implementován.";
        }
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

        // 2b. Načti zápis z prohlídky (user notes z user_listing_state)
        var inspectionNotes = await LoadInspectionNotesAsync(listingId, ct);
        if (inspectionNotes is not null)
            logger.LogInformation("Načten zápis z prohlídky ({Len} znaků) pro {ListingId}", inspectionNotes.Length, listingId);

        // 3. Sestav systémový prompt ze šablony
        var systemPrompt = BuildSystemPrompt(listing);

        // 4. Sestav user zprávu (popis + footy)
        var userMessage = BuildUserMessage(listing, photoDescriptions);

        // 5. Vygeneruj analýzu
        var effectiveModel = chatModel?.Trim();
        logger.LogInformation("Generuji analýzu pro {ListingId} pomocí modelu {Model}...",
            listingId, effectiveModel ?? "výchozí (config)");

        var analysisMarkdown = effectiveModel is not null
            ? effectiveModel.StartsWith("groq-tools/", StringComparison.OrdinalIgnoreCase)
                ? await GroqToolingAsync(
                    effectiveModel["groq-tools/".Length..],
                    listingId, listing, inspectionNotes, systemPrompt, ct)
                : effectiveModel.StartsWith("groq/", StringComparison.OrdinalIgnoreCase)
                ? await ExternalOpenAiChatAsync(
                    GroqBaseUrl,
                    GroqApiKey ?? throw new InvalidOperationException("Groq:ApiKey není nastaven."),
                    effectiveModel["groq/".Length..],
                    systemPrompt, userMessage, ct)
                : effectiveModel.StartsWith("mistral-tools/", StringComparison.OrdinalIgnoreCase)
                    ? await MistralToolingAsync(
                        effectiveModel["mistral-tools/".Length..],
                        listingId, listing, inspectionNotes, systemPrompt, ct)
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

    // ─── DOCX via DocumentFormat.OpenXml (pure .NET, žádné ext. závislosti) ─
    private Task<byte[]?> ConvertMarkdownToDocxAsync(
        string markdownContent, string title, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var wordDoc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                mainPart.Document = new Document(new Body());
                var body = mainPart.Document.Body!;

                // Název dokumentu jako H1
                body.AppendChild(DocxHeading(title, 1));

                // Markdown → DOCX
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                var mdDoc = Markdown.Parse(markdownContent, pipeline);
                DocxRenderBlocks(body, mdDoc, depth: 0);

                // Stránkování A4, okraje 2 cm
                body.AppendChild(new SectionProperties(
                    new PageSize { Width = 11906U, Height = 16838U },
                    new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134 }));

                mainPart.Document.Save();
            }
            return Task.FromResult<byte[]?>(ms.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DOCX generování selhalo");
            return Task.FromResult<byte[]?>(null);
        }
    }

    // ─── OpenXml block renderers ──────────────────────────────────────────────

    private static void DocxRenderBlocks(Body body, IEnumerable<Block> blocks, int depth)
    {
        foreach (var block in blocks)
            DocxRenderBlock(body, block, depth);
    }

    private static void DocxRenderBlock(Body body, Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock h:
                body.AppendChild(DocxHeading(DocxGetText(h.Inline), h.Level + 1));
                break;

            case ParagraphBlock p:
                body.AppendChild(DocxParagraph(p.Inline, indent: depth * 360));
                break;

            case ListBlock list:
                DocxRenderList(body, list, depth);
                break;

            case FencedCodeBlock fenced:
                body.AppendChild(DocxCode(fenced.Lines.ToString()));
                break;

            case CodeBlock code:
                body.AppendChild(DocxCode(code.Lines.ToString()));
                break;

            case ThematicBreakBlock:
                body.AppendChild(DocxHorizontalRule());
                break;

            case Block b when b.GetType().FullName == "Markdig.Extensions.Tables.Table":
                body.AppendChild(DocxTableFromMarkdig(b));
                break;

            case ContainerBlock container:
                DocxRenderBlocks(body, container, depth);
                break;
        }
    }

    private static void DocxRenderList(Body body, ListBlock list, int depth)
    {
        int idx = 1;
        foreach (ListItemBlock item in list.Cast<ListItemBlock>())
        {
            foreach (var child in item)
            {
                if (child is ParagraphBlock lp)
                {
                    string bullet = list.IsOrdered ? $"{idx}." : "•";
                    body.AppendChild(DocxListItem(lp.Inline, bullet, depth));
                }
                else if (child is ListBlock nested)
                {
                    DocxRenderList(body, nested, depth + 1);
                }
                else
                {
                    DocxRenderBlock(body, child, depth + 1);
                }
            }
            idx++;
        }
    }

    // ─── OpenXml paragraph factories ─────────────────────────────────────────

    private static Paragraph DocxHeading(string text, int level)
    {
        (int pts, string color, int spaceBefore, int spaceAfter) = level switch
        {
            1 => (36, "2F5496", 480, 160),
            2 => (28, "2E75B6", 320, 100),
            3 => (24, "1F3864", 200, 80),
            _ => (22, "404040", 160, 60),
        };
        return new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines
                {
                    Before   = spaceBefore.ToString(),
                    After    = spaceAfter.ToString(),
                    Line     = "276",
                    LineRule = LineSpacingRuleValues.Auto,
                }),
            new Run(
                new RunProperties(
                    new Bold(),
                    new Color { Val = color },
                    new FontSize { Val = (pts * 2).ToString() },
                    new FontSizeComplexScript { Val = (pts * 2).ToString() },
                    new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph DocxParagraph(ContainerInline? inline, int indent = 0)
    {
        var pPr = new ParagraphProperties(
            new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto });
        if (indent > 0)
            pPr.AppendChild(new Indentation { Left = indent.ToString() });

        var para = new Paragraph(pPr);
        foreach (var run in DocxInlineRuns(inline))
            para.AppendChild(run);
        return para;
    }

    private static Paragraph DocxListItem(ContainerInline? inline, string bullet, int depth)
    {
        int indent  = 360 + depth * 360;
        int hanging = 360;
        var pPr = new ParagraphProperties(
            new SpacingBetweenLines { After = "60", Line = "276", LineRule = LineSpacingRuleValues.Auto },
            new Indentation { Left = (indent + hanging).ToString(), Hanging = hanging.ToString() });

        var para = new Paragraph(pPr);
        para.AppendChild(new Run(
            new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }),
            new Text(bullet + "\t") { Space = SpaceProcessingModeValues.Preserve }));
        foreach (var run in DocxInlineRuns(inline))
            para.AppendChild(run);
        return para;
    }

    private static Paragraph DocxCode(string text)
    {
        var pPr = new ParagraphProperties(
            new SpacingBetweenLines { Before = "80", After = "80" },
            new Indentation { Left = "720" });
        return new Paragraph(pPr,
            new Run(
                new RunProperties(
                    new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
                    new FontSize { Val = "18" },
                    new Color { Val = "555555" }),
                new Text(text.Trim()) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Paragraph DocxHorizontalRule()
    {
        return new Paragraph(
            new ParagraphProperties(
                new ParagraphBorders(
                    new BottomBorder
                    {
                        Val   = BorderValues.Single,
                        Size  = 6U,
                        Color = "AAAAAA",
                        Space = 4U,
                    }),
                new SpacingBetweenLines { Before = "120", After = "120" }));
    }

    // ─── Markdig table → OpenXml Table (reflection-free, via duck-typing) ────

    private static Table DocxTableFromMarkdig(Block mdTableBlock)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder    { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" },
                new LeftBorder   { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" },
                new RightBorder  { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" },
                new InsideVerticalBorder   { Val = BorderValues.Single, Size = 4U, Color = "AAAACC" }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        if (mdTableBlock is not ContainerBlock rows) return table;

        foreach (ContainerBlock mdRow in rows.Cast<ContainerBlock>())
        {
            // TableRow.IsHeader is true for header row
            bool isHeader = mdRow.GetType().GetProperty("IsHeader")?.GetValue(mdRow) is true;

            var row = new TableRow();
            foreach (ContainerBlock mdCell in mdRow.Cast<ContainerBlock>())
            {
                var tcPr = new TableCellProperties(
                    new TableCellMargin(
                        new TopMargin    { Width = "80",  Type = TableWidthUnitValues.Dxa },
                        new BottomMargin { Width = "80",  Type = TableWidthUnitValues.Dxa },
                        new LeftMargin   { Width = "115", Type = TableWidthUnitValues.Dxa },
                        new RightMargin  { Width = "115", Type = TableWidthUnitValues.Dxa }));
                if (isHeader)
                    tcPr.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "E7E6E8" });

                var cellPara = new Paragraph();
                foreach (var child in mdCell)
                {
                    if (child is ParagraphBlock pBlock)
                        foreach (var run in DocxInlineRuns(pBlock.Inline, bold: isHeader))
                            cellPara.AppendChild(run);
                }

                var cell = new TableCell(tcPr, cellPara);
                row.AppendChild(cell);
            }
            table.AppendChild(row);
        }
        return table;
    }

    // ─── OpenXml inline helpers ───────────────────────────────────────────────

    private static IEnumerable<Run> DocxInlineRuns(
        ContainerInline? inline, bool bold = false, bool italic = false)
    {
        if (inline is null) yield break;

        foreach (var node in inline)
        {
            switch (node)
            {
                case LiteralInline lit:
                    yield return DocxRun(lit.Content.ToString(), bold, italic);
                    break;

                case EmphasisInline em:
                    bool b = bold   || em.DelimiterCount >= 2;
                    bool i = italic || em.DelimiterCount == 1;
                    foreach (var r in DocxInlineRuns(em, b, i))
                        yield return r;
                    break;

                case CodeInline code:
                    yield return DocxRun(code.Content, bold, italic, mono: true);
                    break;

                case LineBreakInline:
                    yield return new Run(new Break());
                    break;

                case ContainerInline container:
                    foreach (var r in DocxInlineRuns(container, bold, italic))
                        yield return r;
                    break;
            }
        }
    }

    private static Run DocxRun(string text, bool bold = false, bool italic = false, bool mono = false)
    {
        var rPr = new RunProperties();
        if (bold)   rPr.AppendChild(new Bold());
        if (italic) rPr.AppendChild(new Italic());
        if (mono)
        {
            rPr.AppendChild(new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" });
            rPr.AppendChild(new FontSize { Val = "18" });
            rPr.AppendChild(new Color { Val = "555555" });
        }
        else
        {
            rPr.AppendChild(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" });
        }
        return new Run(rPr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static string DocxGetText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var node in inline)
        {
            switch (node)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code:   sb.Append(code.Content); break;
                case ContainerInline c: sb.Append(DocxGetText(c)); break;
            }
        }
        return sb.ToString();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Načte zápis z osobní prohlídky z tabulky user_listing_state.</summary>
    private async Task<string?> LoadInspectionNotesAsync(Guid listingId, CancellationToken ct)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT notes FROM re_realestate.user_listing_state " +
                "WHERE listing_id = @id AND notes IS NOT NULL AND notes <> '' LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id"; p.Value = listingId;
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DBNull or null ? null : result.ToString();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Nepodařilo se načíst inspection notes pro {ListingId}", listingId);
            return null;
        }
    }

    /// <summary>
    /// Dynamicky sestav user prompt pro tool-use modely (Groq, Mistral).
    /// Sekce se přizpůsobí datům inzerátu a zápisu z prohlídky – žádné hardcoded detaily.
    /// </summary>
    private static string BuildToolUserPrompt(Listing listing, string? inspectionNotes)
    {
        var sb          = new StringBuilder();
        var notesLower  = inspectionNotes?.ToLowerInvariant() ?? "";
        var hasLand     = listing.AreaLand.HasValue && listing.AreaLand > 0;
        var hasInherit  = notesLower.Contains("dědic") || notesLower.Contains("dědictv");
        var hasDelayed  = notesLower.Contains("převzetí") || notesLower.Contains("měsíc") || notesLower.Contains("mesíc");
        var hasWell     = notesLower.Contains("studna")
                          || listing.Description?.ToLowerInvariant().Contains("studna") == true;

        sb.AppendLine($"Proveď kompletní analýzu nemovitosti (ID: {listing.Id}).");
        sb.AppendLine("Nejprve zavolej get_listing_details + get_photo_descriptions + get_cadastre_data.");
        sb.AppendLine("Pak napiš analýzu ČESKY (s diakritikou) v tomto pořadí sekcí:");
        sb.AppendLine();
        sb.AppendLine("1. Základní parametry (tabulka: adresa, dispozice, plocha m², pozemek m², cena Kč, Kč/m², typ stavby, stav)");
        sb.AppendLine("2. Konkrétní VADY a RIZIKA — bullet list (vlhkost, střecha, elektrika, konstrukční závady…)");
        if (hasInherit)
            sb.AppendLine("   ⚠️ DĚDICKÉ ŘÍZENÍ: analyzuj rizika, doporuč podmínky v kupní smlouvě a odhadni dobu trvání");
        sb.AppendLine("3. Hodnocení ceny:");
        sb.AppendLine("   - tržní Kč/m²: srovnání průměrné ceny m² v lokalitě");
        sb.AppendLine("   - maximální nabídková cena: X Kč");
        sb.AppendLine("   - CELKOVÁ INVESTICE = kupní cena + daň + notář + opravy");
        sb.AppendLine("   - hodnota po rekonstrukci: X Kč");
        sb.AppendLine(hasLand
            ? "4. Yield: hrubý výnos X % · ROI: návratnost investice X let (uveď obé jako číslo – POVINNÉ)"
            : "4. Yield a ROI: odhadni hrubý výnos z pronájmu (%) · odhadni návratnost investice (roky)");
        sb.AppendLine("5. Bodovací tabulka — každé kritérium formátem \"X/5\": Lokalita X/5 · Stav X/5 · Cena X/5 · Potenciál X/5 · SKÓRE X/5");
        sb.AppendLine("6. Povodňové riziko lokality · PENB: pokud inzerát neuvádí energetický průkaz, napiš přesně \u201ePENB chybí\u201c");
        sb.AppendLine("7. Specifika parcely a přístupu (tvar, orientace, hranice, věcná břemena na pozemku)");
        if (hasWell)
            sb.AppendLine("   · Studna: doporuč laboratorní rozbor vody před koupí");
        sb.AppendLine(hasDelayed
            ? "8. Odložené předání: termín dle dohody smluvních stran · smluvní pokuta za prodlení — doporuč zahrnout do kupní smlouvy"
            : "8. Předání nemovitosti: podmínky dle kupní smlouvy · doporuč smluvní pokutu za prodlení");
        sb.AppendLine("9. Due Diligence otázky pro prodávajícího (min. 5 konkrétních bodů)");
        sb.AppendLine("10. Technický stav (tabulka) · katastr: zástavní práva, věcná břemena");
        sb.AppendLine("11. VERDIKT 🟢/🟡/🔴");

        if (!string.IsNullOrWhiteSpace(inspectionNotes))
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("## ZÁPIS Z OSOBNÍ PROHLÍDKY (použij jako primární zdroj faktů a specifik této nemovitosti):");
            sb.AppendLine(inspectionNotes);
        }

        return sb.ToString();
    }

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
