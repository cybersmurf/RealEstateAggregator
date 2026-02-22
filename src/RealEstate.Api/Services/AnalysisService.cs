using RealEstate.Api.Contracts.Analysis;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing AI analysis jobs.
/// TODO: Implement with EF Core DbContext and background job trigger (US-601)
/// </summary>
public class AnalysisService : IAnalysisService
{
    public Task<AnalysisJobDto?> CreateJobForListingAsync(
        Guid listingId,
        AnalysisJobCreateDto request,
        CancellationToken cancellationToken)
    {
        // TODO: Implement job creation
        // 1. Check if listing exists
        // 2. Create AnalysisJob entity with Status = Pending
        // 3. Trigger background job to process
        return Task.FromResult<AnalysisJobDto?>(null);
    }

    public Task<AnalysisJobDto?> GetByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        // TODO: Implement GetById from database
        return Task.FromResult<AnalysisJobDto?>(null);
    }
}
