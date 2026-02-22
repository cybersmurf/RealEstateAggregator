using RealEstate.Api.Contracts.Analysis;

namespace RealEstate.Api.Services;

public interface IAnalysisService
{
    Task<AnalysisJobDto?> CreateJobForListingAsync(
        Guid listingId,
        AnalysisJobCreateDto request,
        CancellationToken cancellationToken);

    Task<AnalysisJobDto?> GetByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken);
}
