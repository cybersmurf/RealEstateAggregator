using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Market;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Market;

/// <summary>
/// Tržní statistiky počítané v paměti nad projekcí inzerátů (aktivní + stažené za posledních 12 měsíců,
/// bez duplikátů). Dataset má řádově tisíce řádků, mediány a percentily se proto počítají v C#.
/// </summary>
public sealed partial class MarketStatsService(
    RealEstateDbContext db,
    ILogger<MarketStatsService> logger) : IMarketStatsService
{
    // ── Meze věrohodnosti ──────────────────────────────────────────────────────
    private const decimal BuildingMinPerM2 = 3_000m;
    private const decimal BuildingMaxPerM2 = 300_000m;
    private const decimal LandMinPerM2 = 50m;
    private const decimal LandMaxPerM2 = 20_000m;
    private const decimal RentMinPerM2 = 50m;
    private const decimal RentMaxPerM2 = 1_000m;

    /// <summary>Pásmo „kolem mediánu“ (±10 %).</summary>
    public const double VerdictBandPct = 10.0;

    private const int MinLocalSample = 5;
    private const int MinRegionSample = 8;
    private const int MinYieldSample = 3;
    private const int MinReportActive = 3;
    private const int MaxReportRows = 300;

    /// <summary>Projekce inzerátu pro výpočty (načítá se AsNoTracking).</summary>
    private sealed record Row(
        Guid Id,
        string? Municipality,
        string? District,
        string? Region,
        string LocationText,
        PropertyType PropertyType,
        OfferType OfferType,
        decimal? Price,
        double? AreaBuiltUp,
        double? AreaLand,
        string? Disposition,
        bool IsActive,
        DateTime FirstSeenAt,
        DateTime? DeactivatedAt)
    {
        public string Locality => LocalityKey(Municipality, LocationText);
        public decimal? PricePerM2 => MarketStatsService.PricePerM2(PropertyType, Price, AreaBuiltUp, AreaLand);
        public decimal? RentPerM2 => MarketStatsService.RentPerM2(Price, AreaBuiltUp);
        public string? DispositionKey => NormalizeDisposition(Disposition);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Cena za m² vs. lokalita
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<MarketComparisonDto?> GetComparisonAsync(Guid listingId, CancellationToken ct)
    {
        var target = await LoadRowAsync(listingId, ct);
        if (target is null)
            return null;

        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var sample = await CandidateQuery(cutoff)
            .Where(l => l.PropertyType == target.PropertyType && l.OfferType == target.OfferType && l.Id != target.Id)
            .ToListAsync(ct);

        var priced = sample
            .Select(r => (Row: r, PerM2: r.PricePerM2))
            .Where(x => x.PerM2 is not null)
            .Select(x => (x.Row, PerM2: x.PerM2!.Value))
            .ToList();

        var locality = target.Locality;
        var disposition = target.DispositionKey;
        var dispositionLabel = disposition is null ? "" : $", {disposition}";

        var scopes = new (string Scope, Func<Row, bool> Filter, int Min, string Label)[]
        {
            ("municipality+disposition",
                r => r.Locality == locality && disposition is not null && r.DispositionKey == disposition,
                MinLocalSample, $"{locality}{dispositionLabel}"),
            ("municipality", r => r.Locality == locality, MinLocalSample, locality),
            ("district+disposition",
                r => target.District is not null && r.District == target.District && disposition is not null && r.DispositionKey == disposition,
                MinLocalSample, $"okres {target.District}{dispositionLabel}"),
            ("district", r => target.District is not null && r.District == target.District, MinLocalSample, $"okres {target.District}"),
            ("region", r => target.Region is not null && r.Region == target.Region, MinRegionSample, target.Region ?? ""),
        };

        var listingPerM2 = target.PricePerM2;

        foreach (var (scope, filter, min, label) in scopes)
        {
            var values = priced.Where(x => filter(x.Row)).Select(x => x.PerM2).ToList();
            if (values.Count < min)
                continue;

            var median = Percentile(values, 0.5);
            var deviation = listingPerM2 is null || median == 0 ? (double?)null : DeviationPct(listingPerM2.Value, median);

            return new MarketComparisonDto(
                ListingId: target.Id,
                ListingPricePerM2: listingPerM2.HasValue ? Math.Round(listingPerM2.Value) : null,
                MedianPricePerM2: Math.Round(median),
                P25PricePerM2: Math.Round(Percentile(values, 0.25)),
                P75PricePerM2: Math.Round(Percentile(values, 0.75)),
                SampleSize: values.Count,
                DeviationPct: deviation is null ? null : Math.Round(deviation.Value, 1),
                Scope: scope,
                ScopeLabel: $"{label} ({CountLabel(values.Count)})",
                Verdict: deviation is null ? null : Verdict(deviation.Value));
        }

        logger.LogDebug("Market comparison: nedostatek srovnatelných inzerátů pro {ListingId} ({Locality})", listingId, locality);

        return new MarketComparisonDto(
            ListingId: target.Id,
            ListingPricePerM2: listingPerM2.HasValue ? Math.Round(listingPerM2.Value) : null,
            MedianPricePerM2: null,
            P25PricePerM2: null,
            P75PricePerM2: null,
            SampleSize: 0,
            DeviationPct: null,
            Scope: "none",
            ScopeLabel: $"{locality} – nedostatek srovnatelných inzerátů",
            Verdict: null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Výnos z pronájmu
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<RentalYieldDto?> GetRentalYieldAsync(Guid listingId, CancellationToken ct)
    {
        var target = await LoadRowAsync(listingId, ct);
        if (target is null)
            return null;

        var locality = target.Locality;

        if (target.PropertyType is not (PropertyType.Apartment or PropertyType.House))
            return Empty(target, locality, "Výnos z pronájmu počítáme jen pro byty a domy.");

        if (target.OfferType == OfferType.Auction)
            return Empty(target, locality, "Pro dražby výnos nepočítáme – vyvolávací cena neodpovídá tržní.");

        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var sample = await CandidateQuery(cutoff)
            .Where(l => l.PropertyType == target.PropertyType
                        && (l.OfferType == OfferType.Sale || l.OfferType == OfferType.Rent)
                        && l.Id != target.Id)
            .ToListAsync(ct);

        var rents = sample.Where(r => r.OfferType == OfferType.Rent)
            .Select(r => (Row: r, Value: r.RentPerM2))
            .Where(x => x.Value is not null)
            .Select(x => (x.Row, Value: x.Value!.Value))
            .ToList();
        var sales = sample.Where(r => r.OfferType == OfferType.Sale)
            .Select(r => (Row: r, Value: r.PricePerM2))
            .Where(x => x.Value is not null)
            .Select(x => (x.Row, Value: x.Value!.Value))
            .ToList();

        if (target.OfferType == OfferType.Sale)
        {
            var own = target.PricePerM2;
            if (own is null)
                return Empty(target, locality, "Inzerát nemá věrohodnou cenu za m² (chybí cena nebo plocha).");

            var rent = PickYieldSample(rents, target);
            if (rent is null)
                return new RentalYieldDto(target.Id, Math.Round(own.Value), null, null, 1, 0, locality,
                    "V lokalitě není dostatek inzerátů na pronájem pro odhad nájmu.");

            return new RentalYieldDto(
                ListingId: target.Id,
                SalePricePerM2Median: Math.Round(own.Value),
                MonthlyRentPerM2Median: Math.Round(rent.Value.Median),
                GrossYieldPct: Math.Round(GrossYieldPct(own.Value, rent.Value.Median), 2),
                SaleSampleSize: 1,
                RentSampleSize: rent.Value.Count,
                ScopeLabel: rent.Value.Label,
                Note: "Výnos z vlastní ceny inzerátu a mediánu nájmů v lokalitě.");
        }
        else
        {
            var ownRent = target.RentPerM2;
            if (ownRent is null)
                return Empty(target, locality, "Inzerát nemá věrohodný nájem za m² (chybí cena nebo plocha).");

            var sale = PickYieldSample(sales, target);
            if (sale is null)
                return new RentalYieldDto(target.Id, null, Math.Round(ownRent.Value), null, 0, 1, locality,
                    "V lokalitě není dostatek inzerátů na prodej pro odhad kupní ceny.");

            return new RentalYieldDto(
                ListingId: target.Id,
                SalePricePerM2Median: Math.Round(sale.Value.Median),
                MonthlyRentPerM2Median: Math.Round(ownRent.Value),
                GrossYieldPct: Math.Round(GrossYieldPct(sale.Value.Median, ownRent.Value), 2),
                SaleSampleSize: sale.Value.Count,
                RentSampleSize: 1,
                ScopeLabel: sale.Value.Label,
                Note: "Výnos z nájmu tohoto inzerátu a mediánu prodejních cen v lokalitě.");
        }

        static RentalYieldDto Empty(Row t, string locality, string note) =>
            new(t.Id, null, null, null, 0, 0, locality, note);
    }

    /// <summary>Řetězec lokalita+dispozice → lokalita → okres+dispozice → okres, vždy n ≥ 3.</summary>
    private static (decimal Median, int Count, string Label)? PickYieldSample(
        List<(Row Row, decimal Value)> data, Row target)
    {
        var locality = target.Locality;
        var disposition = target.DispositionKey;
        var dispositionLabel = disposition is null ? "" : $", {disposition}";

        var scopes = new (Func<Row, bool> Filter, string Label)[]
        {
            (r => r.Locality == locality && disposition is not null && r.DispositionKey == disposition, $"{locality}{dispositionLabel}"),
            (r => r.Locality == locality, locality),
            (r => target.District is not null && r.District == target.District && disposition is not null && r.DispositionKey == disposition, $"okres {target.District}{dispositionLabel}"),
            (r => target.District is not null && r.District == target.District, $"okres {target.District}"),
        };

        foreach (var (filter, label) in scopes)
        {
            var values = data.Where(x => filter(x.Row)).Select(x => x.Value).ToList();
            if (values.Count >= MinYieldSample)
                return (Percentile(values, 0.5), values.Count, $"{label} ({CountLabel(values.Count)})");
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Tržní report
    // ═══════════════════════════════════════════════════════════════════════════

    public async Task<MarketReportDto> GetReportAsync(string? municipality, string? district, string? propertyType, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMonths(-12);
        var query = CandidateQuery(cutoff);

        var pt = ParsePropertyType(propertyType);
        if (pt is not null)
            query = query.Where(l => l.PropertyType == pt.Value);

        if (!string.IsNullOrWhiteSpace(district))
        {
            var d = district.Trim();
            query = query.Where(l => l.District != null && EF.Functions.ILike(l.District, $"%{d}%"));
        }

        var rows = await query.ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(municipality))
        {
            var m = municipality.Trim();
            rows = rows.Where(r => r.Locality.Contains(m, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var result = new List<MarketReportRowDto>();

        foreach (var group in rows.GroupBy(r => (r.Locality, r.PropertyType)))
        {
            var row = BuildReportRow(group.Key.Locality, group.Key.PropertyType, null, group.ToList());
            if (row is not null)
                result.Add(row);

            if (group.Key.PropertyType != PropertyType.Apartment)
                continue;

            foreach (var byDisposition in group.Where(r => r.DispositionKey is not null).GroupBy(r => r.DispositionKey!))
            {
                var dispositionRow = BuildReportRow(group.Key.Locality, group.Key.PropertyType, byDisposition.Key, byDisposition.ToList());
                if (dispositionRow is not null)
                    result.Add(dispositionRow);
            }
        }

        var ordered = result
            .OrderByDescending(r => r.ActiveCount)
            .ThenBy(r => r.Locality, StringComparer.Create(new System.Globalization.CultureInfo("cs-CZ"), ignoreCase: true))
            .ThenBy(r => r.Disposition is null ? 0 : 1)
            .Take(MaxReportRows)
            .ToList();

        return new MarketReportDto(
            GeneratedAt: DateTime.UtcNow,
            Rows: ordered,
            TotalActive: rows.Count(r => r.IsActive),
            TotalDeactivatedLast12M: rows.Count(r => !r.IsActive && r.DeactivatedAt is not null && r.DeactivatedAt >= cutoff));
    }

    private static MarketReportRowDto? BuildReportRow(string locality, PropertyType propertyType, string? disposition, List<Row> rows)
    {
        var active = rows.Where(r => r.IsActive).ToList();
        if (active.Count < MinReportActive)
            return null;

        var deactivated = rows.Where(r => !r.IsActive && r.DeactivatedAt is not null).ToList();

        var salePerM2 = rows.Where(r => r.OfferType == OfferType.Sale).Select(r => r.PricePerM2).Where(v => v is not null).Select(v => v!.Value).ToList();
        var rentPerM2 = rows.Where(r => r.OfferType == OfferType.Rent).Select(r => r.RentPerM2).Where(v => v is not null).Select(v => v!.Value).ToList();

        decimal? saleMedian = salePerM2.Count > 0 ? Math.Round(Percentile(salePerM2, 0.5)) : null;
        decimal? rentMedian = rentPerM2.Count > 0 ? Math.Round(Percentile(rentPerM2, 0.5)) : null;
        double? yield = saleMedian is > 0 && rentMedian is not null ? Math.Round(GrossYieldPct(saleMedian.Value, rentMedian.Value), 2) : null;

        var now = DateTime.UtcNow;
        var daysToDeactivate = deactivated.Select(r => Math.Max(0, (r.DeactivatedAt!.Value - r.FirstSeenAt).TotalDays)).ToList();
        var daysActive = active.Select(r => Math.Max(0, (now - r.FirstSeenAt).TotalDays)).ToList();

        return new MarketReportRowDto(
            Locality: locality,
            PropertyType: propertyType.ToString(),
            Disposition: disposition,
            ActiveCount: active.Count,
            SoldLast12M: deactivated.Count,
            MedianPricePerM2Sale: saleMedian,
            MedianRentPerM2: rentMedian,
            GrossYieldPct: yield,
            MedianDaysToDeactivate: daysToDeactivate.Count > 0 ? Math.Round(Percentile(daysToDeactivate, 0.5), 1) : null,
            MedianDaysOnMarketActive: daysActive.Count > 0 ? Math.Round(Percentile(daysActive, 0.5), 1) : null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Načítání
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Aktivní inzeráty + stažené za posledních 12 měsíců, bez duplikátů.</summary>
    private IQueryable<Row> CandidateQuery(DateTime cutoff) =>
        db.Listings.AsNoTracking()
            .Where(l => l.DuplicateOfListingId == null
                        && (l.IsActive || (l.DeactivatedAt != null && l.DeactivatedAt >= cutoff)))
            .Select(l => new Row(
                l.Id, l.Municipality, l.District, l.Region, l.LocationText,
                l.PropertyType, l.OfferType, l.Price, l.AreaBuiltUp, l.AreaLand,
                l.Disposition, l.IsActive, l.FirstSeenAt, l.DeactivatedAt));

    private Task<Row?> LoadRowAsync(Guid listingId, CancellationToken ct) =>
        db.Listings.AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => new Row(
                l.Id, l.Municipality, l.District, l.Region, l.LocationText,
                l.PropertyType, l.OfferType, l.Price, l.AreaBuiltUp, l.AreaLand,
                l.Disposition, l.IsActive, l.FirstSeenAt, l.DeactivatedAt))
            .FirstOrDefaultAsync(ct);

    // ═══════════════════════════════════════════════════════════════════════════
    // Čisté pomocné funkce (public static kvůli testům)
    // ═══════════════════════════════════════════════════════════════════════════

    [GeneratedRegex(@"Brno\s*[-–]\s*([^,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrnoPartRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Klíč lokality: u Brna městská část z LocationText („Brno - Židenice, Táborská“ → „Brno-Židenice“),
    /// jinak obec; bez obce první segment LocationText před čárkou.
    /// </summary>
    public static string LocalityKey(string? municipality, string? locationText)
    {
        var mun = municipality?.Trim();
        var text = locationText?.Trim();

        var isBrno = string.IsNullOrEmpty(mun) || mun.StartsWith("Brno", StringComparison.OrdinalIgnoreCase);
        if (isBrno && !string.IsNullOrEmpty(text))
        {
            var match = BrnoPartRegex().Match(text);
            if (match.Success)
            {
                var part = WhitespaceRegex().Replace(match.Groups[1].Value.Trim(), " ");
                if (part.Length > 0)
                    return $"Brno-{part}";
            }
        }

        if (!string.IsNullOrEmpty(mun))
            return mun;

        if (string.IsNullOrEmpty(text))
            return "Neznámá lokalita";

        var first = text.Split(',', 2)[0].Trim();
        return first.Length > 0 ? first : "Neznámá lokalita";
    }

    /// <summary>Cena za m² podle typu (stavby → zastavěná/užitná plocha, pozemek → plocha pozemku) s filtrem věrohodnosti.</summary>
    public static decimal? PricePerM2(PropertyType propertyType, decimal? price, double? areaBuiltUp, double? areaLand)
    {
        if (price is null or <= 0)
            return null;

        if (propertyType == PropertyType.Land)
            return InRange(price.Value, areaLand, LandMinPerM2, LandMaxPerM2);

        if (propertyType is PropertyType.Apartment or PropertyType.House or PropertyType.Cottage)
            return InRange(price.Value, areaBuiltUp, BuildingMinPerM2, BuildingMaxPerM2);

        // Komerční, průmysl, garáž, ostatní – vezmeme užitnou plochu se stavebními mezemi
        return InRange(price.Value, areaBuiltUp, BuildingMinPerM2, BuildingMaxPerM2);
    }

    /// <summary>Měsíční nájem za m² s filtrem věrohodnosti (50–1 000 Kč/m²/měsíc).</summary>
    public static decimal? RentPerM2(decimal? monthlyRent, double? areaBuiltUp)
    {
        if (monthlyRent is null or <= 0)
            return null;
        return InRange(monthlyRent.Value, areaBuiltUp, RentMinPerM2, RentMaxPerM2);
    }

    private static decimal? InRange(decimal price, double? area, decimal min, decimal max)
    {
        if (area is null or <= 0)
            return null;
        var perM2 = price / (decimal)area.Value;
        return perM2 < min || perM2 > max ? null : perM2;
    }

    /// <summary>Percentil s lineární interpolací (p ∈ ⟨0;1⟩). Prázdný vstup vyhodí ArgumentException.</summary>
    public static decimal Percentile(IEnumerable<decimal> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            throw new ArgumentException("Percentil nelze spočítat z prázdné množiny.", nameof(values));

        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];

        var weight = (decimal)(rank - lower);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            throw new ArgumentException("Percentil nelze spočítat z prázdné množiny.", nameof(values));

        var rank = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    public static decimal Median(IEnumerable<decimal> values) => Percentile(values, 0.5);

    /// <summary>Odchylka od mediánu v procentech (kladná = dražší než medián).</summary>
    public static double DeviationPct(decimal value, decimal median) =>
        median == 0 ? 0 : (double)((value - median) / median * 100m);

    /// <summary>Slovní verdikt: pásmo ±<see cref="VerdictBandPct"/> % = „kolem mediánu“.</summary>
    public static string Verdict(double deviationPct) => deviationPct switch
    {
        < -VerdictBandPct => "pod mediánem",
        > VerdictBandPct => "nad mediánem",
        _ => "kolem mediánu",
    };

    /// <summary>Hrubý roční výnos: nájem/m²/měsíc × 12 ÷ cena/m² × 100.</summary>
    public static double GrossYieldPct(decimal salePricePerM2, decimal monthlyRentPerM2) =>
        salePricePerM2 <= 0 ? 0 : (double)(monthlyRentPerM2 * 12m / salePricePerM2 * 100m);

    /// <summary>„2+kk“, „2 + KK“, „2+KK“ → „2+kk“.</summary>
    public static string? NormalizeDisposition(string? disposition)
    {
        if (string.IsNullOrWhiteSpace(disposition))
            return null;
        var d = WhitespaceRegex().Replace(disposition.Trim(), "").ToLowerInvariant();
        return d.Length == 0 ? null : d;
    }

    /// <summary>České skloňování: 1 inzerát, 2–4 inzeráty, 5+ inzerátů.</summary>
    public static string CountLabel(int count) => count switch
    {
        1 => "1 inzerát",
        >= 2 and <= 4 => $"{count} inzeráty",
        _ => $"{count} inzerátů",
    };

    private static PropertyType? ParsePropertyType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "house" => PropertyType.House,
        "apartment" => PropertyType.Apartment,
        "land" => PropertyType.Land,
        "cottage" => PropertyType.Cottage,
        "commercial" => PropertyType.Commercial,
        "industrial" => PropertyType.Industrial,
        "garage" => PropertyType.Garage,
        "other" => PropertyType.Other,
        _ => null,
    };
}
