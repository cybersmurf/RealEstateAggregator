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
}
