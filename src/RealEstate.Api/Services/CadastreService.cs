using System.Net.Http.Json;
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
