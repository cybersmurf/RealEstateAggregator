using RealEstate.Api.Contracts.Sources;
using RealEstate.Api.Endpoints;

namespace RealEstate.Api.Services;

/// <summary>
/// Service for managing scraping sources.
/// TODO: Implement with EF Core DbContext (US-203)
/// </summary>
public class SourceService : ISourceService
{
    public Task<IReadOnlyList<SourceDto>> GetSourcesAsync(
        SourceFilterParameters filter,
        CancellationToken cancellationToken)
    {
        // TODO: Implement GetSources from database
        IReadOnlyList<SourceDto> result = Array.Empty<SourceDto>();
        return Task.FromResult(result);
    }
}
