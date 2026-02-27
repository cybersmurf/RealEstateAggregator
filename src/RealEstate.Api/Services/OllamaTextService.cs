using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Textové Ollama featury nad inzeráty:
///   • Smart tags (5 tagů z popisu)
///   • Normalizace popisu (rok stavby, patro, výtah, sklep, zahrada…)
///   • Cenový signál (low / fair / high)
///   • Detekce duplikátů (porovnání dvou inzerátů)
///
/// Používá IEmbeddingService.ChatAsync() → llama3.2 / qwen2.5 text model.
/// </summary>
public sealed class OllamaTextService(
    RealEstateDbContext db,
    IEmbeddingService embedding,
    ILogger<OllamaTextService> logger) : IOllamaTextService
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ─── Smart Tags ───────────────────────────────────────────────────────────

    private const string SmartTagsSystem = """
        You are a Czech real estate data extractor.
        Extract exactly 5 short keyword tags from the listing description.
        Tags must be in Czech, lowercase, max 2 words each.
        Focus on: property features, amenities, construction type, condition, extras.
        Examples: sklep, zahrada, garáž, novostavba, rekonstrukce, výtah, bazén, rohový byt, podkroví, terasa.
        Respond ONLY with valid JSON array: ["tag1","tag2","tag3","tag4","tag5"]
        If fewer than 5 relevant tags exist, fill remaining slots with the most relevant general tags.
        """;

    public async Task<OllamaTextBatchResultDto> BulkSmartTagsAsync(int batchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        var listings = await db.Listings
            .Where(l => l.SmartTags == null && l.Description != null && l.Description.Length > 50)
            .OrderBy(l => l.FirstSeenAt)
            .Take(batchSize)
            .ToListAsync(ct);

        return await ProcessBatchAsync(listings, ct, async (listing, c) =>
        {
            var userMsg = $"Název: {listing.Title}\nPopis: {listing.Description![..Math.Min(2000, listing.Description.Length)]}";
            var response = await embedding.ChatAsync(SmartTagsSystem, userMsg, c);

            var tags = TryParseJsonArray(response);
            if (tags is null || tags.Count == 0)
            {
                logger.LogWarning("SmartTags: invalid JSON for listing {Id}: {Raw}", listing.Id,
                    response[..Math.Min(200, response.Length)]);
                return false;
            }

            listing.SmartTags = JsonSerializer.Serialize(tags.Take(5));
            listing.SmartTagsAt = DateTime.UtcNow;
            return true;
        }, "smart_tags");
    }

    // ─── Description Normalization ────────────────────────────────────────────

    private const string NormalizeSystem = """
        You are a Czech real estate data extractor.
        From the listing description, extract structured data as JSON.
        Respond ONLY with valid JSON (no explanation):
        {
          "year_built": 1985,
          "floor": 2,
          "total_floors": 4,
          "has_elevator": false,
          "has_basement": true,
          "has_garage": false,
          "has_garden": false,
          "has_balcony": false,
          "has_terrace": false,
          "has_pool": false,
          "heating_type": "gas",
          "energy_class": "C",
          "ownership": "personal",
          "is_single_floor": false,
          "has_storage": false,
          "extension_possible": false
        }
        Use null for unknown values. heating_type: gas|electric|solid_fuel|heat_pump|district|other.
        ownership: personal|cooperative|company|state.
        energy_class: A|B|C|D|E|F|G or null.
        is_single_floor: true if the house is entirely on one level (bungalov, přízemní, přízemí, bez schodů).
        has_storage: true if listing mentions storage space (sklep, sklad, půda, garáž, přístřešek).
        extension_possible: true if there is potential for extension or loft conversion (vestavba, přístavba, podkroví, atip).
        """;

    public async Task<OllamaTextBatchResultDto> BulkNormalizeAsync(int batchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        var listings = await db.Listings
            .Where(l => l.AiNormalizedData == null && l.Description != null && l.Description.Length > 100)
            .OrderBy(l => l.FirstSeenAt)
            .Take(batchSize)
            .ToListAsync(ct);

        return await ProcessBatchAsync(listings, ct, async (listing, c) =>
        {
            var userMsg = $"Název: {listing.Title}\nLocalita: {listing.LocationText}\nPopis: {listing.Description![..Math.Min(2500, listing.Description.Length)]}";
            var response = await embedding.ChatAsync(NormalizeSystem, userMsg, c);

            var json = TryParseJson(response);
            if (json is null)
            {
                logger.LogWarning("Normalize: invalid JSON for listing {Id}: {Raw}", listing.Id,
                    response[..Math.Min(200, response.Length)]);
                return false;
            }

            listing.AiNormalizedData = json;
            listing.AiNormalizedAt = DateTime.UtcNow;
            return true;
        }, "normalize");
    }

    // ─── Price Opinion ────────────────────────────────────────────────────────

    private const string PriceOpinionSystem = """
        You are a Czech real estate price analyst.
        Based on the listing metadata (location, size, condition, type), assess if the price is:
          - "low"  → below typical market value for this type/location
          - "fair" → roughly aligned with market value
          - "high" → above typical market value

        Important context: Czech Republic market, 2024-2025 prices.
        Typical ranges per m² (built-up area):
          - Prague: 80 000–150 000 CZK/m²
          - Brno: 50 000–90 000 CZK/m²
          - Regional cities (Znojmo, Třebíč): 20 000–45 000 CZK/m²
          - Villages: 10 000–25 000 CZK/m²
          - Land (per m²): 500–5 000 CZK/m²

        Respond ONLY with valid JSON:
        {"signal": "fair", "reason": "Cena 3 200 000 Kč za 85 m² v Znojmě odpovídá cca 37 600 Kč/m², což je v normálním rozsahu pro lokalitu."}
        signal must be exactly "low", "fair", or "high".
        reason must be in Czech, max 200 characters.
        """;

    public async Task<OllamaTextBatchResultDto> BulkPriceOpinionAsync(int batchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 50);

        // Zpracuj jen inzeráty s cenou, bez existujícího signálu
        var listings = await db.Listings
            .Where(l => l.PriceSignal == null && l.Price != null && l.Price > 0)
            .OrderBy(l => l.FirstSeenAt)
            .Take(batchSize)
            .ToListAsync(ct);

        return await ProcessBatchAsync(listings, ct, async (listing, c) =>
        {
            var area = listing.AreaBuiltUp > 0 ? listing.AreaBuiltUp : listing.AreaLand;
            var pricePerM2 = area > 0 ? listing.Price / (decimal)area!.Value : null;

            var userMsg = $"""
                Typ: {listing.PropertyType} | Nabídka: {listing.OfferType}
                Cena: {listing.Price:N0} Kč{(pricePerM2 != null ? $" ({pricePerM2:N0} Kč/m²)" : "")}
                Plocha: {(listing.AreaBuiltUp > 0 ? $"{listing.AreaBuiltUp} m² (zastavěná)" : listing.AreaLand > 0 ? $"{listing.AreaLand} m² (pozemek)" : "neznámá")}
                Lokalita: {listing.LocationText}
                Stav: {listing.Condition ?? "neznámý"}
                Dispozice: {listing.Disposition ?? "neznámá"}
                Název: {listing.Title}
                """;

            var response = await embedding.ChatAsync(PriceOpinionSystem, userMsg, c);
            var parsed = TryParseJsonObject<PriceOpinionJson>(response);

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Signal)
                || parsed.Signal is not ("low" or "fair" or "high"))
            {
                logger.LogWarning("PriceOpinion: invalid response for listing {Id}: {Raw}", listing.Id,
                    response[..Math.Min(200, response.Length)]);
                return false;
            }

            listing.PriceSignal = parsed.Signal;
            listing.PriceSignalReason = parsed.Reason is { } r
                ? r[..Math.Min(500, r.Length)]
                : null;
            listing.PriceSignalAt = DateTime.UtcNow;
            return true;
        }, "price_opinion");
    }

    // ─── Duplicate Detection ──────────────────────────────────────────────────

    private const string DuplicateSystem = """
        You are a Czech real estate duplicate detector.
        Compare two listings and determine if they describe the SAME physical property.
        Consider: address/location similarity, size, price, description overlap, photos.
        A duplicate means same property listed by different agencies or scrapers.

        Respond ONLY with valid JSON:
        {"is_duplicate": true, "confidence": 0.92, "reasoning": "Stejná adresa, plocha 85 m², cena jen o 5% odlišná, totožný popis dispozice."}
        confidence: 0.0–1.0
        reasoning must be in Czech, max 250 characters.
        """;

    public async Task<DuplicateDetectionResultDto> DetectDuplicatesAsync(
        Guid listingId1, Guid listingId2, CancellationToken ct)
    {
        var l1 = await db.Listings.FindAsync([listingId1], ct);
        var l2 = await db.Listings.FindAsync([listingId2], ct);

        if (l1 is null || l2 is null)
            throw new ArgumentException($"Jeden nebo oba inzeráty neexistují: {listingId1}, {listingId2}");

        var userMsg = $"""
            Inzerát 1:
              Název: {l1.Title}
              Lokalita: {l1.LocationText}
              Cena: {l1.Price:N0} Kč | Plocha: {l1.AreaBuiltUp ?? l1.AreaLand} m² | Dispozice: {l1.Disposition}
              Zdroj: {l1.SourceCode} | ID: {l1.ExternalId}
              Popis (začátek): {l1.Description[..Math.Min(800, l1.Description.Length)]}

            Inzerát 2:
              Název: {l2.Title}
              Lokalita: {l2.LocationText}
              Cena: {l2.Price:N0} Kč | Plocha: {l2.AreaBuiltUp ?? l2.AreaLand} m² | Dispozice: {l2.Disposition}
              Zdroj: {l2.SourceCode} | ID: {l2.ExternalId}
              Popis (začátek): {l2.Description[..Math.Min(800, l2.Description.Length)]}
            """;

        var response = await embedding.ChatAsync(DuplicateSystem, userMsg, ct);
        var parsed = TryParseJsonObject<DuplicateJson>(response);

        if (parsed is null)
        {
            logger.LogWarning("Duplicate: invalid JSON for {Id1} vs {Id2}: {Raw}",
                listingId1, listingId2, response[..Math.Min(200, response.Length)]);
            return new DuplicateDetectionResultDto(listingId1, listingId2, false, 0,
                "Chyba: nepodařilo se zpracovat odpověď modelu.");
        }

        logger.LogInformation(
            "Duplicate check {Id1} vs {Id2}: isDuplicate={Dup}, confidence={Conf}",
            listingId1, listingId2, parsed.IsDuplicate, parsed.Confidence);

        return new DuplicateDetectionResultDto(
            listingId1, listingId2,
            parsed.IsDuplicate,
            Math.Clamp(parsed.Confidence, 0.0, 1.0),
            parsed.Reasoning ?? "");
    }

    // ─── Stats ────────────────────────────────────────────────────────────────

    public async Task<OllamaTextStatsDto> GetStatsAsync(CancellationToken ct)
    {
        var total   = await db.Listings.CountAsync(ct);
        var tags    = await db.Listings.CountAsync(l => l.SmartTags != null, ct);
        var norm    = await db.Listings.CountAsync(l => l.AiNormalizedData != null, ct);
        var price   = await db.Listings.CountAsync(l => l.PriceSignal != null, ct);
        var low     = await db.Listings.CountAsync(l => l.PriceSignal == "low", ct);
        var fair    = await db.Listings.CountAsync(l => l.PriceSignal == "fair", ct);
        var high    = await db.Listings.CountAsync(l => l.PriceSignal == "high", ct);

        return new OllamaTextStatsDto(total, tags, norm, price, low, fair, high);
    }

    // ─── Shared batch runner ──────────────────────────────────────────────────

    private async Task<OllamaTextBatchResultDto> ProcessBatchAsync(
        List<Listing> listings,
        CancellationToken ct,
        Func<Listing, CancellationToken, Task<bool>> processOne,
        string featureName)
    {
        if (listings.Count == 0)
            return new OllamaTextBatchResultDto(0, 0, 0, 0, 0);

        int succeeded = 0, failed = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var listing in listings)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await processOne(listing, ct);
                if (ok) succeeded++;
                else failed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "OllamaText {Feature} failed for listing {Id}", featureName, listing.Id);
                failed++;
            }
        }

        if (succeeded > 0)
            await db.SaveChangesAsync(ct);

        sw.Stop();
        var avgMs = listings.Count > 0 ? sw.ElapsedMilliseconds / (double)listings.Count : 0;

        // Spočítej zbývající (přibližně – nezahrnuje batch filtr)
        var remaining = featureName switch
        {
            "smart_tags"    => await db.Listings.CountAsync(l => l.SmartTags == null && l.Description != null && l.Description.Length > 50, ct),
            "normalize"     => await db.Listings.CountAsync(l => l.AiNormalizedData == null && l.Description != null && l.Description.Length > 100, ct),
            "price_opinion" => await db.Listings.CountAsync(l => l.PriceSignal == null && l.Price != null && l.Price > 0, ct),
            _ => 0
        };

        logger.LogInformation(
            "OllamaText {Feature}: {Proc} processed, {Ok} OK, {Fail} failed, {Rem} remaining. Avg {Avg:F0} ms",
            featureName, listings.Count, succeeded, failed, remaining, avgMs);

        return new OllamaTextBatchResultDto(listings.Count, succeeded, failed, remaining, avgMs);
    }

    // ─── JSON helpers ─────────────────────────────────────────────────────────

    private static List<string>? TryParseJsonArray(string raw)
    {
        // Najdi první [ ... ] v odpovědi (model někdy přidá výzvu nebo markdown)
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw[start..(end + 1)], _json);
        }
        catch { return null; }
    }

    private static string? TryParseJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var candidate = raw[start..(end + 1)];
        try
        {
            // Ověř platnost JSON (pokud se podaří deserializovat na JsonDocument)
            using var doc = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch { return null; }
    }

    private static T? TryParseJsonObject<T>(string raw)
    {
        var candidate = TryParseJson(raw);
        if (candidate is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(candidate, _json);
        }
        catch { return default; }
    }

    // ─── Private DTOs for JSON parsing ────────────────────────────────────────

    private sealed class PriceOpinionJson
    {
        [JsonPropertyName("signal")] public string? Signal { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    private sealed class DuplicateJson
    {
        [JsonPropertyName("is_duplicate")] public bool IsDuplicate { get; set; }
        [JsonPropertyName("confidence")]   public double Confidence { get; set; }
        [JsonPropertyName("reasoning")]    public string? Reasoning { get; set; }
    }
}
