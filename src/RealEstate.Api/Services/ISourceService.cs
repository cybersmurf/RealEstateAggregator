using RealEstate.Api.Contracts.Sources;
using RealEstate.Api.Endpoints;

namespace RealEstate.Api.Services;

public interface ISourceService
{
    Task<IReadOnlyList<SourceDto>> GetSourcesAsync(
        SourceFilterParameters filter,
        CancellationToken cancellationToken);
}
