using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Market;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Market;

public interface ILocalityStatsService
{
    Task<IReadOnlyList<LocalityIndexItemDto>> GetIndexAsync(int minActive, CancellationToken ct);
    Task<LocalityPublicStatsDto?> GetPublicStatsAsync(string slug, CancellationToken ct);
}

/// <summary>
/// Veřejné statistiky obcí pro SEO stránky. Záměrně jen agregáty z vlastních dat
/// (počty, mediány, doba na trhu) – žádné texty ani fotky zdrojů.
/// </summary>
public sealed class LocalityStatsService(RealEstateDbContext db) : ILocalityStatsService
{
    private sealed record Row(
        string? Municipality, string? LocationText, string? District, PropertyType PropertyType, OfferType OfferType,
        decimal? Price, double? AreaBuiltUp, double? AreaLand, bool IsActive, DateTime FirstSeenAt, DateTime? DeactivatedAt);

    public async Task<IReadOnlyList<LocalityIndexItemDto>> GetIndexAsync(int minActive, CancellationToken ct)
    {
        var rows = await db.Listings.AsNoTracking()
            .Where(l => l.IsActive && l.DuplicateOfListingId == null)
            .Select(l => new { l.Municipality, l.LocationText, l.District })
            .ToListAsync(ct);

        return rows
            .Select(r => new { Key = MarketStatsService.LocalityKey(r.Municipality, r.LocationText), r.District })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !char.IsDigit(x.Key[0]))
            .GroupBy(x => x.Key)
            .Select(g => new LocalityIndexItemDto(
                g.Key, Slugify(g.Key),
                g.Select(x => x.District).Where(d => d is not null).GroupBy(d => d).OrderByDescending(d => d.Count()).Select(d => d.Key).FirstOrDefault(),
                g.Count()))
            .Where(x => x.ActiveCount >= minActive)
            .OrderByDescending(x => x.ActiveCount).ThenBy(x => x.Name)
            .ToList();
    }

    public async Task<LocalityPublicStatsDto?> GetPublicStatsAsync(string slug, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddMonths(-12);
        var all = await db.Listings.AsNoTracking()
            .Where(l => l.DuplicateOfListingId == null && (l.IsActive || l.DeactivatedAt >= since))
            .Select(l => new Row(l.Municipality, l.LocationText, l.District, l.PropertyType, l.OfferType,
                l.Price, l.AreaBuiltUp, l.AreaLand, l.IsActive, l.FirstSeenAt, l.DeactivatedAt))
            .ToListAsync(ct);

        var rows = all.Where(r => Slugify(MarketStatsService.LocalityKey(r.Municipality, r.LocationText)) == slug).ToList();
        if (rows.Count == 0)
            return null;

        var name = MarketStatsService.LocalityKey(rows[0].Municipality, rows[0].LocationText);
        var active = rows.Where(r => r.IsActive).ToList();
        var now = DateTime.UtcNow;

        decimal? MedianOf(IEnumerable<decimal> values)
        {
            var list = values.OrderBy(v => v).ToList();
            return list.Count >= 3 ? MarketStatsService.Median(list) : null;
        }

        IEnumerable<decimal> PerM2(IEnumerable<Row> src, Func<Row, double?> area, decimal min, decimal max) =>
            src.Where(r => r.Price is > 0 && area(r) is > 0)
               .Select(r => r.Price!.Value / (decimal)area(r)!.Value)
               .Where(v => v >= min && v <= max);

        var houses = active.Where(r => r.PropertyType == PropertyType.House && r.OfferType == OfferType.Sale).ToList();
        var land = active.Where(r => r.PropertyType == PropertyType.Land && r.OfferType == OfferType.Sale).ToList();
        var flatsSale = active.Where(r => r.PropertyType == PropertyType.Apartment && r.OfferType == OfferType.Sale).ToList();
        var flatsRent = active.Where(r => r.PropertyType == PropertyType.Apartment && r.OfferType == OfferType.Rent).ToList();
        var deactivated = rows.Where(r => !r.IsActive && r.DeactivatedAt is not null).ToList();

        var priceDrops = await db.ListingPriceHistories.AsNoTracking()
            .Where(h => h.RecordedAt >= DateTimeOffset.UtcNow.AddDays(-90) && h.Source == "scraper"
                        && h.Listing.IsActive
                        && ((h.Listing.Municipality != null && h.Listing.Municipality == name)
                            || (h.Listing.Municipality == null && h.Listing.LocationText.StartsWith(name))))
            .Select(h => h.ListingId).Distinct().CountAsync(ct);

        return new LocalityPublicStatsDto(
            name, slug,
            active.Select(r => r.District).Where(d => d is not null).GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(),
            active.Count, houses.Count, land.Count, flatsSale.Count,
            active.Count(r => r.OfferType == OfferType.Rent),
            active.Count(r => r.FirstSeenAt >= now.AddDays(-30)),
            deactivated.Count,
            MedianOf(houses.Where(r => r.Price is > 0).Select(r => r.Price!.Value)),
            MedianOf(PerM2(houses, r => r.AreaBuiltUp, 3_000, 300_000)),
            MedianOf(PerM2(land, r => r.AreaLand, 50, 20_000)),
            MedianOf(PerM2(flatsSale, r => r.AreaBuiltUp, 3_000, 300_000)),
            MedianOf(PerM2(flatsRent, r => r.AreaBuiltUp, 50, 1_000)),
            active.Count >= 3 ? MarketStatsService.Percentile(active.Select(r => (now - r.FirstSeenAt).TotalDays), 0.5) : null,
            deactivated.Count >= 3 ? MarketStatsService.Percentile(deactivated.Select(r => (r.DeactivatedAt!.Value - r.FirstSeenAt).TotalDays), 0.5) : null,
            priceDrops,
            now);
    }

    /// <summary>URL slug bez diakritiky: "Hrušovany nad Jevišovkou" → "hrusovany-nad-jevisovkou".</summary>
    public static string Slugify(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
