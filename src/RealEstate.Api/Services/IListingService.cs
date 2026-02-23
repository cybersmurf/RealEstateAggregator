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
}
