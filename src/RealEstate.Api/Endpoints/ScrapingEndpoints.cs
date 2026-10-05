using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Scraping;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class ScrapingEndpoints
{
    public static RouteGroupBuilder MapScrapingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scraping")
            .WithTags("Scraping");

        group.MapPost("/trigger", TriggerScrape)
            .WithName("TriggerScrape");

        group.MapGet("/jobs/{jobId:guid}", GetJob)
            .WithName("GetScrapeJob");

        group.MapGet("/jobs", GetRecentJobs)
            .WithName("GetScrapeJobs");

        return group;
    }

    private static async Task<Ok<ScrapeTriggerResultDto>> TriggerScrape(
        [FromBody] ScrapeTriggerDto request,
        [FromServices] IScrapingService scrapingService,
        CancellationToken cancellationToken)
    {
        var result = await scrapingService.TriggerScrapeAsync(request, cancellationToken);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ScrapeJobDto>, NotFound>> GetJob(
        Guid jobId,
        [FromServices] IScrapingService scrapingService,
        CancellationToken cancellationToken)
    {
        var job = await scrapingService.GetJobAsync(jobId, cancellationToken);
        return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
    }

    private static async Task<Ok<IReadOnlyList<ScrapeJobDto>>> GetRecentJobs(
        [FromServices] IScrapingService scrapingService,
        CancellationToken cancellationToken,
        int limit = 5)
        => TypedResults.Ok(await scrapingService.GetRecentJobsAsync(Math.Clamp(limit, 1, 50), cancellationToken));
}
