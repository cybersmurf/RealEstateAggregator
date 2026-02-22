using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Analysis;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis")
            .WithTags("Analysis");

        group.MapPost("/listing/{listingId:guid}", CreateAnalysisJob)
            .WithName("CreateAnalysisJob");

        group.MapGet("/{jobId:guid}", GetAnalysisJobById)
            .WithName("GetAnalysisJobById");

        return app;
    }

    private static async Task<Results<Ok<AnalysisJobDto>, NotFound>> CreateAnalysisJob(
        Guid listingId,
        [FromBody] AnalysisJobCreateDto request,
        [FromServices] IAnalysisService analysisService,
        CancellationToken cancellationToken)
    {
        var job = await analysisService.CreateJobForListingAsync(listingId, request, cancellationToken);
        if (job is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(job);
    }

    private static async Task<Results<Ok<AnalysisJobDto>, NotFound>> GetAnalysisJobById(
        Guid jobId,
        [FromServices] IAnalysisService analysisService,
        CancellationToken cancellationToken)
    {
        var job = await analysisService.GetByIdAsync(jobId, cancellationToken);
        if (job is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(job);
    }
}
