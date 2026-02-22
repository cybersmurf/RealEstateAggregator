using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Sources;
using RealEstate.Api.Endpoints;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing scraping sources with database access.
/// </summary>
public class SourceService : ISourceService
{
    private readonly RealEstateDbContext _dbContext;

    public SourceService(RealEstateDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<SourceDto>> GetSourcesAsync(
        SourceFilterParameters filter,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.Sources.AsQueryable();

        // Apply filters
        if (filter.OnlyActive.HasValue && filter.OnlyActive.Value)
        {
            query = query.Where(s => s.IsActive);
        }

        var sources = await query
            .OrderBy(s => s.Name)
            .Select(s => new SourceDto
            {
                Id = s.Id,
                Code = s.Code,
                Name = s.Name,
                BaseUrl = s.BaseUrl,
                IsActive = s.IsActive
            })
            .ToListAsync(cancellationToken);

        return sources.AsReadOnly();
    }
}
