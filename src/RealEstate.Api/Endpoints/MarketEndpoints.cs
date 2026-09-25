using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Market;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Market;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// Tržní statistiky – Kč/m² vs. lokalita (tarif Hledač), výnos z nájmu a tržní report (tarif Profi).
/// </summary>
public static class MarketEndpoints
{
    public static IEndpointRouteBuilder MapMarketEndpoints(this IEndpointRouteBuilder app)
    {
        var listings = app.MapGroup("/api/listings")
            .WithTags("Market")
            .WithOpenApi();

        listings.MapGet("/{id:guid}/market-comparison", GetMarketComparison)
            .WithName("GetMarketComparison")
            .WithSummary("Cena za m² inzerátu vs. medián srovnatelných inzerátů v lokalitě")
            .RequirePlan(UserPlans.Hledac);

        listings.MapGet("/{id:guid}/rental-yield", GetRentalYield)
            .WithName("GetRentalYield")
            .WithSummary("Hrubý výnos z pronájmu (nájem × 12 / kupní cena) pro byt/dům")
            .RequirePlan(UserPlans.Profi);

        var market = app.MapGroup("/api/market")
            .WithTags("Market")
            .WithOpenApi();

        market.MapGet("/report", GetMarketReport)
            .WithName("GetMarketReport")
            .WithSummary("Tržní report po lokalitách: aktivní, stažené, medián Kč/m², výnos, doba na trhu")
            .RequirePlan(UserPlans.Profi);

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<MarketComparisonDto>, NotFound>> GetMarketComparison(
        Guid id,
        [FromServices] IMarketStatsService service,
        CancellationToken ct)
    {
        var result = await service.GetComparisonAsync(id, ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<RentalYieldDto>, NotFound>> GetRentalYield(
        Guid id,
        [FromServices] IMarketStatsService service,
        CancellationToken ct)
    {
        var result = await service.GetRentalYieldAsync(id, ct);
        return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
    }

    private static async Task<Ok<MarketReportDto>> GetMarketReport(
        [FromQuery] string? municipality,
        [FromQuery] string? district,
        [FromQuery] string? propertyType,
        [FromServices] IMarketStatsService service,
        CancellationToken ct)
    {
        var result = await service.GetReportAsync(municipality, district, propertyType, ct);
        return TypedResults.Ok(result);
    }
}
