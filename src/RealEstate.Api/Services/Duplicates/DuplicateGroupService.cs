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

    public async Task<GroupPhotoSet> GetGroupPhotoSetAsync(Guid listingId, CancellationToken ct)
    {
        var anchor = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => new { l.Id, l.SourceCode, l.DuplicateOfListingId })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        var primaryId = anchor.DuplicateOfListingId ?? anchor.Id;

        var members = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == primaryId || l.DuplicateOfListingId == primaryId)
            .Select(l => new PhotoOwnerCandidate(l.Id, l.SourceCode, l.Photos.Count, l.FirstSeenAt, l.IsActive))
            .ToListAsync(ct);

        var owner = ChoosePhotoOwner(listingId, primaryId, members) ?? listingId;
        var ownerCode = members.FirstOrDefault(m => m.Id == owner)?.SourceCode ?? anchor.SourceCode;

        var photos = await db.ListingPhotos
            .AsNoTracking()
            .Where(p => p.ListingId == owner)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);

        return new GroupPhotoSet(owner, ownerCode, photos, IsBorrowed: owner != listingId);
    }

    public sealed record PhotoOwnerCandidate(Guid Id, string SourceCode, int PhotoCount, DateTime FirstSeenAt, bool IsActive);

    /// <summary>
    /// Vlastník fotek: nejvíc fotek; při shodě sám inzerát, pak primár, pak nejstarší.
    /// Stažené inzeráty jen když žádný aktivní fotky nemá (URL stažených bývají mrtvé).
    /// </summary>
    public static Guid? ChoosePhotoOwner(Guid listingId, Guid primaryId, IReadOnlyList<PhotoOwnerCandidate> members)
    {
        if (members.Count == 0) return null;
        var pool = members.Where(m => m.IsActive && m.PhotoCount > 0).ToList();
        if (pool.Count == 0) pool = members.Where(m => m.PhotoCount > 0).ToList();
        if (pool.Count == 0) return listingId;

        return pool
            .OrderByDescending(m => m.PhotoCount)
            .ThenByDescending(m => m.Id == listingId)
            .ThenByDescending(m => m.Id == primaryId)
            .ThenBy(m => m.FirstSeenAt)
            .ThenBy(m => m.Id)
            .First().Id;
    }
}
