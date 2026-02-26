using RealEstate.Api.Contracts.Common;
using RealEstate.Api.Contracts.Listings;

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

    /// <summary>Exportuje výsledky vyhledávání jako CSV (max 5000 záznámů).</summary>
    Task<byte[]> ExportCsvAsync(ListingFilterDto filter, CancellationToken cancellationToken);

    /// <summary>Vrátí všechny inzeráty tagované uživatelem (stav != New), seskupené dle stavu.</summary>
    Task<MyListingsSummaryDto> GetMyListingsAsync(CancellationToken cancellationToken);
}
