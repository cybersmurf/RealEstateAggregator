using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Scraping;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// Endpoints for Playwright-based scraping (.NET native alternative to Python scraper).
/// </summary>
public static class ScrapingPlaywrightEndpoints
{
    public static IEndpointRouteBuilder MapScrapingPlaywrightEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/scraping-playwright")
            .WithTags("ScrapingPlaywright");

        group.MapPost("/run", RunPlaywrightScrape)
            .WithName("RunPlaywrightScrape")
            .WithDescription("Spustí Playwright scraping job v .NET (alternativa k Python API)");

        return app;
    }

    private static async Task<Ok<ScrapeTriggerResultDto>> RunPlaywrightScrape(
        [FromBody] ScrapeTriggerDto request,
        [FromServices] IPlaywrightScrapingOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.RunAsync(request, cancellationToken);
        return TypedResults.Ok(result);
    }
}
