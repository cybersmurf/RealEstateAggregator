using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Cadastre;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Integrace s ČÚZK RUIAN (Registr územní identifikace, adres a nemovitostí).
///
/// Workflow:
///   1. FetchAndSaveAsync – dotaz na RUIAN ArcGIS REST API dle adresy inzerátu
///   2. Sestavení přímého odkazu na nahlížení.cuzk.cz
///   3. Uložení do listing_cadastre_data
///   4. SaveManualDataAsync – doplnění LV, břemen, výměry manuálně
/// </summary>
public sealed class CadastreService(
    RealEstateDbContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<CadastreService> logger) : ICadastreService
{
    // ─── Konfigurace ──────────────────────────────────────────────────────────
    private const string RuianFindUrl =
        "https://ags.cuzk.cz/arcgis/rest/services/RUIAN/"
        + "Vyhledavaci_sluzba_nad_daty_RUIAN/MapServer/find";

    private const string NahlizenidoknBase = "https://nahlizenidokn.cuzk.cz";

    // ─── READ ─────────────────────────────────────────────────────────────────
    public async Task<ListingCadastreDto?> GetAsync(Guid listingId, CancellationToken ct = default)
    {
        var row = await db.ListingCadastreData
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ListingId == listingId, ct);

        return row is null ? null : ToDto(row);
    }

    // ─── RUIAN FETCH ──────────────────────────────────────────────────────────
    public async Task<ListingCadastreDto> FetchAndSaveAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await db.Listings
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen.");

        // Adresa pro vyhledávání – preferuj municipality, fallback na location_text
        var searchText = PreferMunicipality(listing.Municipality, listing.LocationText);
        var (ruianKod, cadastreUrl, status, error, rawJson) = await CallRuianAsync(searchText, ct);

        // Upsert
        var existing = await db.ListingCadastreData
            .FirstOrDefaultAsync(x => x.ListingId == listingId, ct);

        if (existing is null)
        {
            existing = new ListingCadastreData { ListingId = listingId };
            db.ListingCadastreData.Add(existing);
        }

        existing.AddressSearched = searchText;
        existing.RuianKod = ruianKod;
        existing.CadastreUrl = cadastreUrl;
        existing.FetchStatus = status;
        existing.FetchError = error;
        existing.RawRuianJson = rawJson;
        existing.FetchedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("RUIAN fetch listingId={ListingId} → status={Status}, ruianKod={RuianKod}",
            listingId, status, ruianKod);

        return ToDto(existing);
    }

    // ─── MANUAL SAVE ──────────────────────────────────────────────────────────
    public async Task<ListingCadastreDto> SaveManualDataAsync(
        Guid listingId,
        SaveCadastreDataRequest request,
        CancellationToken ct = default)
    {
        var existing = await db.ListingCadastreData
            .FirstOrDefaultAsync(x => x.ListingId == listingId, ct);

        if (existing is null)
        {
            // Potřebujeme aspoň adresu z listingu
            var listing = await db.Listings.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == listingId, ct)
                ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen.");

            existing = new ListingCadastreData
            {
                ListingId = listingId,
                AddressSearched = PreferMunicipality(listing.Municipality, listing.LocationText),
                FetchStatus = "manual",
            };
            db.ListingCadastreData.Add(existing);
        }

        // Aktualizuj jen manuální pole
        existing.ParcelNumber    = request.ParcelNumber ?? existing.ParcelNumber;
        existing.LvNumber        = request.LvNumber ?? existing.LvNumber;
        existing.LandAreaM2      = request.LandAreaM2 ?? existing.LandAreaM2;
        existing.LandType        = request.LandType ?? existing.LandType;
        existing.OwnerType       = request.OwnerType ?? existing.OwnerType;
        existing.EncumbrancesJson = request.EncumbrancesJson ?? existing.EncumbrancesJson;

        await db.SaveChangesAsync(ct);
        return ToDto(existing);
    }

    // ─── BULK FETCH ───────────────────────────────────────────────────────────
    public async Task<BulkRuianResultDto> BulkFetchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        // Inzeráty bez katastrálních dat nebo s error/pending stavem
        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => l.IsActive &&
                !db.ListingCadastreData.Any(c =>
                    c.ListingId == l.Id &&
                    (c.FetchStatus == "found" || c.FetchStatus == "manual" || c.FetchStatus == "not_found")))
            .Take(batchSize)
            .Select(l => new { l.Id, l.LocationText, l.Municipality })
            .ToListAsync(ct);

        int found = 0, notFound = 0, error = 0;

        foreach (var item in listings)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await FetchAndSaveAsync(item.Id, ct);
                var saved = await db.ListingCadastreData
                    .AsNoTracking()
                    .FirstAsync(x => x.ListingId == item.Id, ct);

                switch (saved.FetchStatus)
                {
                    case "found": found++; break;
                    case "not_found": notFound++; break;
                    default: error++; break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bulk RUIAN selhal pro listing {ListingId}", item.Id);
                error++;
            }

            // Rate limiting – RUIAN ArcGIS API
            await Task.Delay(1100, ct);
        }

        var msg = $"Zpracováno {listings.Count}: nalezeno {found}, nenalezeno {notFound}, chyby {error}";
        return new BulkRuianResultDto(listings.Count, found, notFound, error, 0, msg);
    }

    // ─── OCR SCREENSHOT ───────────────────────────────────────────────────────
    public async Task<CadastreOcrResultDto> OcrScreenshotAsync(
        Guid listingId, byte[] imageData, CancellationToken ct = default)
    {
        var base64 = Convert.ToBase64String(imageData);

        var ollamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? configuration["Ollama:BaseUrl"]
            ?? "http://localhost:11434";
        var visionModel = Environment.GetEnvironmentVariable("OLLAMA_VISION_MODEL")
            ?? configuration["Ollama:VisionModel"]
            ?? "llama3.2-vision:11b";

        var ollamaUrl = $"{ollamaBaseUrl.TrimEnd('/')}/api/generate";

        const string ocrPrompt = """
            You are analyzing a screenshot from the Czech cadastre system (nahlizeni.cuzk.cz / nahlizenidokn.cuzk.cz).
            Extract ALL available data from the screenshot and return ONLY valid JSON, nothing else.

            Look for these sections and fields:
            - "Informace o pozemku" or "Informace o budově": parcel/plot details
            - "Vlastníci, jiní oprávnění": owner information
            - "Omezení vlastnického práva": encumbrances / restrictions
            - "Způsob ochrany nemovitosti": protection status
            - "Součástí je stavba": building info

            Return exactly this JSON structure (use null for missing fields):
            {
              "parcel_number": "60",
              "lv_number": "1088",
              "land_area_m2": 593,
              "land_type": "zastavěná plocha a nádvoří",
              "municipality": "Štítary",
              "cadastral_area": "Štítary na Moravě",
              "owner_info": "fyzická osoba",
              "protection": "pam. zóna - budova, pozemek v památkové zóně",
              "encumbrances": [
                {"type": "Věcné břemeno zřizování a provozování vedení"},
                {"type": "Zákaz zcizení"},
                {"type": "Zástavní právo smluvní"},
                {"type": "Zástavní právo z rozhodnutí správního orgánu"}
              ],
              "building_number": "113",
              "building_type": "zemědělská usedlost"
            }

            Important: encumbrances array is critical - list ALL items from "Omezení vlastnického práva" table.
            If there are no encumbrances, use empty array [].
            """;

        var requestBody = new
        {
            model = visionModel,
            prompt = ocrPrompt,
            images = new[] { base64 },
            stream = false,
            format = "json",
            options = new { temperature = 0.1, num_predict = 1024 }
        };

        using var httpClient = httpClientFactory.CreateClient("OllamaVision");
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        logger.LogInformation("KN OCR: calling Ollama Vision for listing {ListingId}", listingId);
        var response = await httpClient.PostAsync(ollamaUrl, jsonContent, ct);
        response.EnsureSuccessStatusCode();

        var rawBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(rawBody);
        var ocrRawJson = doc.RootElement.TryGetProperty("response", out var respEl)
            ? respEl.GetString() ?? "{}"
            : "{}";

        logger.LogInformation("KN OCR raw response for {ListingId}: {Raw}",
            listingId, ocrRawJson[..Math.Min(300, ocrRawJson.Length)]);

        // Parsuj extrahovaná data
        KnOcrData? ocr = null;
        try
        {
            ocr = JsonSerializer.Deserialize<KnOcrData>(ocrRawJson, _ocrJsonOptions);
        }
        catch (JsonException jex)
        {
            logger.LogWarning(jex, "KN OCR: JSON parse failed, trying regex fallback");
            // Pokus o extract z obalujícího textu
            var match = System.Text.RegularExpressions.Regex.Match(ocrRawJson, @"\{[\s\S]+\}");
            if (match.Success)
                try { ocr = JsonSerializer.Deserialize<KnOcrData>(match.Value, _ocrJsonOptions); }
                catch { /* skip */ }
        }

        // Upsert do DB
        var existing = await db.ListingCadastreData
            .FirstOrDefaultAsync(x => x.ListingId == listingId, ct);

        if (existing is null)
        {
            var listing = await db.Listings.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == listingId, ct);
            existing = new ListingCadastreData
            {
                ListingId = listingId,
                AddressSearched = listing?.Municipality ?? listing?.LocationText ?? "",
            };
            db.ListingCadastreData.Add(existing);
        }

        if (ocr is not null)
        {
            existing.ParcelNumber     = ocr.ParcelNumber ?? existing.ParcelNumber;
            existing.LvNumber         = ocr.LvNumber ?? existing.LvNumber;
            existing.LandAreaM2       = ocr.LandAreaM2 ?? existing.LandAreaM2;
            existing.LandType         = ocr.LandType ?? existing.LandType;
            existing.OwnerType        = ocr.OwnerInfo ?? existing.OwnerType;
            if (ocr.Encumbrances?.Count > 0)
                existing.EncumbrancesJson = JsonSerializer.Serialize(ocr.Encumbrances, _ocrJsonOptions);
        }

        existing.FetchStatus  = "ocr";
        existing.FetchError   = null;
        existing.FetchedAt    = DateTime.UtcNow;
        existing.RawRuianJson = ocrRawJson;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("KN OCR saved for listing {ListingId}: parcel={Parcel}, LV={Lv}, area={Area}",
            listingId, existing.ParcelNumber, existing.LvNumber, existing.LandAreaM2);

        return new CadastreOcrResultDto(ToDto(existing), ocrRawJson);
    }

    // ─── Private OCR types ────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _ocrJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed class KnOcrData
    {
        [JsonPropertyName("parcel_number")] public string? ParcelNumber { get; set; }
        [JsonPropertyName("lv_number")]     public string? LvNumber { get; set; }
        [JsonPropertyName("land_area_m2")]  public int? LandAreaM2 { get; set; }
        [JsonPropertyName("land_type")]     public string? LandType { get; set; }
        [JsonPropertyName("municipality")]  public string? Municipality { get; set; }
        [JsonPropertyName("cadastral_area")]public string? CadastralArea { get; set; }
        [JsonPropertyName("owner_info")]    public string? OwnerInfo { get; set; }
        [JsonPropertyName("protection")]    public string? Protection { get; set; }
        [JsonPropertyName("encumbrances")]  public List<KnEncumbrance>? Encumbrances { get; set; }
        [JsonPropertyName("building_number")]  public string? BuildingNumber { get; set; }
        [JsonPropertyName("building_type")]    public string? BuildingType { get; set; }
    }

    private sealed class KnEncumbrance
    {
        [JsonPropertyName("type")]        public string Type { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("who")]         public string? Who { get; set; }
    }

    // ─── PRIVATE HELPERS ──────────────────────────────────────────────────────

    private async Task<(long? RuianKod, string CadastreUrl, string Status, string? Error, string? RawJson)>
        CallRuianAsync(string searchText, CancellationToken ct)
    {
        var url = $"{RuianFindUrl}?searchText={Uri.EscapeDataString(searchText)}" +
                  "&contains=true&layers=2&returnGeometry=false&f=json";

        try
        {
            var client = httpClientFactory.CreateClient("Ruian");
            var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return (null, $"{NahlizenidoknBase}/", "not_found", null, rawJson);

            // Hledáme kód adresního místa
            var attrs = results[0].GetProperty("attributes");
            long? kod = null;
            foreach (var key in new[] { "KOD", "kod", "KOD_ADM", "OBJECTID" })
            {
                if (attrs.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
                {
                    kod = val.GetInt64();
                    break;
                }
            }

            if (kod.HasValue)
            {
                var cadastreUrl = $"{NahlizenidoknBase}/ZobrazitMapu/Basic?typeCode=adresniMisto&id={kod}";
                return (kod, cadastreUrl, "found", null, rawJson);
            }

            return (null, $"{NahlizenidoknBase}/", "not_found", null, rawJson);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RUIAN call selhal pro '{SearchText}'", searchText);
            return (null, $"{NahlizenidoknBase}/", "error", ex.Message, null);
        }
    }

    private static string PreferMunicipality(string? municipality, string locationText)
    {
        if (!string.IsNullOrWhiteSpace(municipality))
            return municipality.Trim();

        // Odstraň "okres X", "kraj X" ze location_text
        var cleaned = System.Text.RegularExpressions.Regex
            .Replace(locationText ?? "", @",?\s*(okres|kraj|okr\.)\s+\S+", "")
            .Trim();

        // Zkrať na max 80 znaků
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    private static ListingCadastreDto ToDto(ListingCadastreData x) => new(
        x.Id,
        x.ListingId,
        x.RuianKod,
        x.ParcelNumber,
        x.LvNumber,
        x.LandAreaM2,
        x.LandType,
        x.OwnerType,
        x.EncumbrancesJson,
        x.AddressSearched,
        x.CadastreUrl,
        x.FetchStatus,
        x.FetchError,
        x.FetchedAt
    );
}
