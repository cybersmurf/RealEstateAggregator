using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services.Market;

namespace RealEstate.Api.Endpoints;

/// <summary>Veřejné agregáty obcí pro SEO stránky a sitemap – bez tarifu.</summary>
public static class LocalityEndpoints
{
    public static IEndpointRouteBuilder MapLocalityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/localities").WithTags("Localities");

        group.MapGet("", async ([FromQuery] int? minActive, [FromServices] ILocalityStatsService svc, CancellationToken ct)
                => Results.Ok(await svc.GetIndexAsync(Math.Clamp(minActive ?? 3, 1, 1000), ct)))
            .WithName("ListLocalities")
            .WithSummary("Seznam obcí s počtem aktivních inzerátů (výchozí min. 3).");

        group.MapGet("/{slug}", async (string slug, [FromServices] ILocalityStatsService svc, CancellationToken ct) =>
            {
                var stats = await svc.GetPublicStatsAsync(slug.ToLowerInvariant(), ct);
                return stats is null ? Results.NotFound() : Results.Ok(stats);
            })
            .WithName("GetLocalityStats")
            .WithSummary("Veřejná statistika obce: počty, mediány Kč/m², doba na trhu.");

        return app;
    }
}
