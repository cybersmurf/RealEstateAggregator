using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services;

public interface IListingService
{
    Task<PagedResultDto<ListingSummaryDto>> SearchAsync(
        ListingFilterDto filter,
        CancellationToken cancellationToken);

    Task<ListingDetailDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<ListingUserStateDto?> UpdateUserStateAsync(
        Guid listingId,
        ListingUserStateUpdateDto request,
        CancellationToken cancellationToken);

    Task<ListingStatsDto> GetStatsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sestaví dotaz na aktivní inzeráty podle filtru (duplikáty skryté), bez řazení a stránkování.
    /// Používají ho uložená hledání pro počet shod a vyhodnocení nových inzerátů.
    /// </summary>
    IQueryable<Listing> BuildFilteredQuery(ListingFilterDto filter);

    /// <summary>Exportuje výsledky vyhledávání jako CSV (max 5000 záznámů).</summary>
    Task<byte[]> ExportCsvAsync(ListingFilterDto filter, CancellationToken cancellationToken);

    /// <summary>Vrátí všechny inzeráty tagované uživatelem (stav != New), seskupené dle stavu.</summary>
    Task<MyListingsSummaryDto> GetMyListingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Vrátí historii cen inzerátu seřazenou od nejstarší po nejnovější.
    /// Vrátí null pokud inzerát neexistuje.
    /// </summary>
    Task<List<PriceHistoryDto>?> GetPriceHistoryAsync(Guid listingId, CancellationToken cancellationToken);

    /// <summary>
    /// Zkontroluje aktivní inzeráty, u nichž je last_seen_at starší než daysOld dní,
    /// a deaktivuje ty, jejichž zdrojová URL vrací HTTP 404 nebo 410.
    /// </summary>
    Task<DeactivateDeadResult> DeactivateDeadListingsAsync(int daysOld, CancellationToken cancellationToken);
}
