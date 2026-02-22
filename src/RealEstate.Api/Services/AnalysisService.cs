using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Analysis;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing AI analysis jobs.
/// TODO: Implement with EF Core DbContext and background job trigger (US-601)
/// </summary>
public class AnalysisService : IAnalysisService
{
    private static readonly Guid DefaultUserId = new("00000000-0000-0000-0000-000000000001");
    private readonly RealEstateDbContext _dbContext;

    public AnalysisService(RealEstateDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<AnalysisJobDto?> CreateJobForListingAsync(
        Guid listingId,
        AnalysisJobCreateDto request,
        CancellationToken cancellationToken)
    {
        var listingExists = await _dbContext.Listings
            .AsNoTracking()
            .AnyAsync(l => l.Id == listingId, cancellationToken);
        if (!listingExists)
        {
            return null;
        }

        var job = new AnalysisJob
        {
            Id = Guid.NewGuid(),
            ListingId = listingId,
            UserId = DefaultUserId,
            Status = Domain.Enums.AnalysisStatus.Pending,
            StorageProvider = request.StorageProvider,
            RequestedAt = DateTime.UtcNow
        };

        _dbContext.AnalysisJobs.Add(job);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapToDto(job);
    }

    public async Task<AnalysisJobDto?> GetByIdAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var job = await _dbContext.AnalysisJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        return job is null ? null : MapToDto(job);
    }

    private static AnalysisJobDto MapToDto(AnalysisJob job)
    {
        return new AnalysisJobDto
        {
            Id = job.Id,
            ListingId = job.ListingId,
            Status = job.Status.ToString(),
            StorageProvider = job.StorageProvider ?? string.Empty,
            StorageUrl = job.StorageUrl,
            RequestedAt = job.RequestedAt,
            FinishedAt = job.FinishedAt,
            ErrorMessage = job.ErrorMessage
        };
    }
}
