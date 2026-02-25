using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Spatial;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class SpatialEndpoints
{
    public static IEndpointRouteBuilder MapSpatialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spatial")
            .WithTags("Spatial")
            .WithOpenApi();

        // ── Geocoding ─────────────────────────────────────────────────────────
        group.MapGet("/geocode", GeocodeAddress)
            .WithName("GeocodeAddress")
            .WithSummary("Geokóduje adresu (město) pomocí Nominatim");

        // ── Corridor ──────────────────────────────────────────────────────────
        group.MapPost("/corridor", BuildCorridor)
            .WithName("BuildCorridor")
            .WithSummary("Postaví koridor (buffer) kolem trasy – vrátí WKT polygon + počet inzerátů");

        // ── Map points ────────────────────────────────────────────────────────
        group.MapGet("/map-points", GetAllMapPoints)
            .WithName("GetAllMapPoints")
            .WithSummary("Vrátí všechny aktivní inzeráty s GPS pro zobrazení na mapě (max 2000)");

        group.MapPost("/search", SpatialSearch)
            .WithName("SpatialSearch")
            .WithSummary("Vyhledá inzeráty uvnitř WKT polygonu nebo bounding boxu");

        // ── Saved areas ───────────────────────────────────────────────────────
        group.MapGet("/areas", GetSavedAreas)
            .WithName("GetSavedAreas")
            .WithSummary("Vrátí uložené pojmenované prostorové oblasti");

        group.MapGet("/geocode-stats", GetGeocodeStats)
            .WithName("GetGeocodeStats")
            .WithSummary("Statistika GPS kódování inzerátů");

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<object>, NotFound<string>>> GeocodeAddress(
        [FromQuery] string address,
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        var coords = await service.GeocodeAsync(address, ct);
        if (coords is null)
            return TypedResults.NotFound($"Adresa nenalezena: '{address}'");

        return TypedResults.Ok<object>(new { latitude = coords.Value.Lat, longitude = coords.Value.Lon, address });
    }

    private static async Task<Results<Ok<CorridorResultDto>, BadRequest<string>>> BuildCorridor(
        [FromBody] CorridorRequestDto request,
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        try
        {
            var result = await service.BuildCorridorAsync(request, ct);
            return TypedResults.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<ListingMapPointDto>>> GetAllMapPoints(
        [FromQuery] string? propertyType,
        [FromQuery] string? offerType,
        [FromQuery] decimal? priceMax,
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        var result = await service.GetAllMapPointsAsync(propertyType, offerType, priceMax, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<IReadOnlyList<ListingMapPointDto>>, BadRequest<string>>> SpatialSearch(
        [FromBody] SpatialSearchRequestDto request,
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        try
        {
            var result = await service.SearchInAreaAsync(request, ct);
            return TypedResults.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<SpatialAreaDto>>> GetSavedAreas(
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        var areas = await service.GetSavedAreasAsync(ct);
        return TypedResults.Ok(areas);
    }

    private static async Task<Ok<object>> GetGeocodeStats(
        [FromServices] ISpatialService service,
        CancellationToken ct)
    {
        var stats = await service.GetGeocodeStatsAsync(ct);
        return TypedResults.Ok<object>(stats);
    }
}
