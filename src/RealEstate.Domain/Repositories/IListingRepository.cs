using RealEstate.Domain.Entities;

namespace RealEstate.Domain.Repositories;

public interface IListingRepository
{
    /// <summary>
    /// Vrací IQueryable pro pokročilé filtrování a dotazy.
    /// Zahrnuje Include pro Source a Photos; UserStates jen pro daného uživatele
    /// (Guid.Empty = anonym → žádné osobní stavy).
    /// </summary>
    IQueryable<Listing> Query(Guid userId);
    
    Task<Listing?> GetByIdAsync(
        Guid id,
        CancellationToken ct = default);
    
    Task<Listing?> GetBySourceAndExternalIdAsync(
        Guid sourceId,
        string externalId,
        CancellationToken ct = default);
    
    Task<Listing> UpsertAsync(
        Listing listing,
        CancellationToken ct = default);
    
    Task<IReadOnlyList<Listing>> GetActiveListingsAsync(
        Guid? sourceId = null,
        CancellationToken ct = default);
}
