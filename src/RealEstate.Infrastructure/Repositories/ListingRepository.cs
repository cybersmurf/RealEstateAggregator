using LinqKit;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Repositories;

namespace RealEstate.Infrastructure.Repositories;

public sealed class ListingRepository : IListingRepository
{
    private readonly RealEstateDbContext _context;

    public ListingRepository(RealEstateDbContext context)
    {
        _context = context;
    }

    public IQueryable<Listing> Query()
    {
        return _context.Listings
            .AsExpandable() // Důležité pro PredicateBuilder + EF Core
            .Include(l => l.Source)
            .Include(l => l.Photos)
            .Include(l => l.UserStates)
            .AsQueryable();
    }

    public async Task<Listing?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default)
    {
        return await _context.Listings
            .Include(l => l.Source)
            .Include(l => l.Photos)
            .Include(l => l.UserStates)
            .FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    public async Task<Listing?> GetBySourceAndExternalIdAsync(
        Guid sourceId,
        string externalId,
        CancellationToken ct = default)
    {
        return await _context.Listings
            .Include(l => l.Photos)
            .FirstOrDefaultAsync(
                l => l.SourceId == sourceId && l.ExternalId == externalId,
                ct);
    }

    public async Task<Listing> UpsertAsync(
        Listing listing,
        CancellationToken ct = default)
    {
        var existing = await GetBySourceAndExternalIdAsync(
            listing.SourceId,
            listing.ExternalId!,
            ct);

        if (existing is not null)
        {
            // Update existing
            existing.Url = listing.Url;
            existing.Title = listing.Title;
            existing.Description = listing.Description;
            existing.PropertyType = listing.PropertyType;
            existing.OfferType = listing.OfferType;
            existing.Price = listing.Price;
            existing.PriceNote = listing.PriceNote;
            existing.LocationText = listing.LocationText;
            existing.Region = listing.Region;
            existing.District = listing.District;
            existing.Municipality = listing.Municipality;
            existing.AreaBuiltUp = listing.AreaBuiltUp;
            existing.AreaLand = listing.AreaLand;
            existing.Rooms = listing.Rooms;
            existing.HasKitchen = listing.HasKitchen;
            existing.ConstructionType = listing.ConstructionType;
            existing.Condition = listing.Condition;
            existing.UpdatedAtSource = listing.UpdatedAtSource;
            existing.LastSeenAt = DateTime.UtcNow;
            existing.IsActive = true;

            // Update photos
            _context.ListingPhotos.RemoveRange(existing.Photos);
            foreach (var photo in listing.Photos)
            {
                photo.ListingId = existing.Id;
                _context.ListingPhotos.Add(photo);
            }

            await _context.SaveChangesAsync(ct);
            return existing;
        }

        // Insert new
        listing.Id = Guid.NewGuid();
        listing.FirstSeenAt = DateTime.UtcNow;
        listing.LastSeenAt = DateTime.UtcNow;
        listing.IsActive = true;

        foreach (var photo in listing.Photos)
        {
            photo.Id = Guid.NewGuid();
            photo.ListingId = listing.Id;
            photo.CreatedAt = DateTime.UtcNow;
        }

        _context.Listings.Add(listing);
        await _context.SaveChangesAsync(ct);
        return listing;
    }

    public async Task<IReadOnlyList<Listing>> GetActiveListingsAsync(
        Guid? sourceId = null,
        CancellationToken ct = default)
    {
        var query = _context.Listings
            .Include(l => l.Photos)
            .Where(l => l.IsActive);

        if (sourceId.HasValue)
        {
            query = query.Where(l => l.SourceId == sourceId.Value);
        }

        return await query.ToListAsync(ct);
    }
}
