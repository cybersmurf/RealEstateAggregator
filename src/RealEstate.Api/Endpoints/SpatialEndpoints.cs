using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Spatial;
using RealEstate.Api.Helpers;
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

        group.MapPost("/bulk-geocode", BulkGeocode)
            .WithName("BulkGeocode")
            .WithSummary("Geokóduje dávku inzerátů bez GPS přes Nominatim (max batchSize, ~1.1s/req)");

        // ── GPX upload ────────────────────────────────────────────────────────
        group.MapPost("/corridor-from-gpx", BuildCorridorFromGpx)
            .WithName("BuildCorridorFromGpx")
            .WithSummary("Nahraje GPX soubor a postaví koridor (buffer) kolem trasy")
            .DisableAntiforgery();

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

    private static async Task<Ok<BulkGeocodeResultDto>> BulkGeocode(
        [FromServices] ISpatialService service,
        CancellationToken ct,
        [FromQuery] int batchSize = 50)
    {
        var result = await service.BulkGeocodeListingsAsync(batchSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<CorridorResultDto>, BadRequest<string>>> BuildCorridorFromGpx(
        IFormFile gpxFile,
        [FromServices] ISpatialService service,
        CancellationToken ct,
        [FromQuery] int bufferMeters = 5000,
        [FromQuery] string? saveName = null)
    {
        if (gpxFile is null || gpxFile.Length == 0)
            return TypedResults.BadRequest("Žádný GPX soubor nebyl nahrán.");

        if (!gpxFile.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
            return TypedResults.BadRequest("Soubor musí mít příponu .gpx");

        if (bufferMeters is < 100 or > 50_000)
            return TypedResults.BadRequest("bufferMeters musí být mezi 100 a 50 000 metry.");

        GpxParseResult gpx;
        try
        {
            await using var stream = gpxFile.OpenReadStream();
            gpx = GpxParser.Parse(stream);
        }
        catch (InvalidDataException ex)
        {
            return TypedResults.BadRequest($"Chyba při parsování GPX: {ex.Message}");
        }

        try
        {
            var result = await service.BuildCorridorFromLineStringAsync(
                gpx.LineStringWkt,
                bufferMeters,
                gpx.StartLat, gpx.StartLon,
                gpx.EndLat, gpx.EndLon,
                saveName,
                ct);

            return TypedResults.Ok(result);
        }
        catch (Exception ex)
        {
            return TypedResults.BadRequest($"Chyba při stavbě koridoru: {ex.Message}");
        }
    }
}
