using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Sources;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources")
            .WithTags("Sources");

        group.MapGet(string.Empty, GetSources)
            .WithName("GetSources");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<SourceDto>>> GetSources(
        [AsParameters] SourceFilterParameters filter,
        [FromServices] ISourceService sourceService,
        CancellationToken cancellationToken)
    {
        var result = await sourceService.GetSourcesAsync(filter, cancellationToken);
        return TypedResults.Ok(result);
    }
}

public sealed class SourceFilterParameters
{
    public bool? OnlyActive { get; set; }
}
