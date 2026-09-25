using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Duplicates;

public sealed class DuplicateGroupService(RealEstateDbContext db) : IDuplicateGroupService
{
    public async Task<DuplicateGroupDto?> GetGroupAsync(Guid listingId, CancellationToken ct)
    {
        var anchor = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => new { l.Id, l.DuplicateOfListingId })
            .FirstOrDefaultAsync(ct);

        if (anchor is null)
            return null;

        var primaryId = anchor.DuplicateOfListingId ?? anchor.Id;

        // Primární + všechny jeho duplikáty, aktivní i stažené (stažené nesou DeactivatedAt)
        var rows = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == primaryId || l.DuplicateOfListingId == primaryId)
            .Select(l => new
            {
                l.Id,
                l.SourceCode,
                l.SourceName,
                l.Url,
                l.Title,
                l.Price,
                l.FirstSeenAt,
                l.LastSeenAt,
                l.IsActive,
                l.DeactivatedAt,
                // Lokální kopie fotek se po klasifikaci mažou – náhled bereme ze zdroje
                ThumbnailUrl = l.Photos.OrderBy(p => p.Order).Select(p => p.OriginalUrl).FirstOrDefault(),
                PhotoCount = l.Photos.Count,
            })
            .ToListAsync(ct);

        if (rows.Count == 0)
            return null;

        var items = rows
            .OrderBy(r => r.Id == primaryId ? 0 : 1)
            .ThenBy(r => r.FirstSeenAt)
            .Select(r => new DuplicateGroupItemDto(
                r.Id,
                r.SourceCode,
                r.SourceName,
                r.Url,
                r.Title,
                r.Price,
                r.FirstSeenAt,
                r.LastSeenAt,
                r.IsActive,
                r.DeactivatedAt,
                IsPrimary: r.Id == primaryId,
                IsCurrent: r.Id == listingId,
                r.ThumbnailUrl,
                r.PhotoCount))
            .ToList();

        var prices = items.Where(i => i.Price is > 0).Select(i => i.Price!.Value).ToList();

        return new DuplicateGroupDto(
            primaryId,
            items.Min(i => i.FirstSeenAt),
            prices.Count > 0 ? prices.Min() : null,
            prices.Count > 0 ? prices.Max() : null,
            items.Select(i => i.SourceCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            items);
    }
}
