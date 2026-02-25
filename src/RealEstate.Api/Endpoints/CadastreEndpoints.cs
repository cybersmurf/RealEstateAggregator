using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Cadastre;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class CadastreEndpoints
{
    public static IEndpointRouteBuilder MapCadastreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cadastre")
            .WithTags("Cadastre")
            .WithOpenApi();

        // ── Čtení ─────────────────────────────────────────────────────────────
        group.MapGet("/listings/{listingId:guid}", GetCadastreData)
            .WithName("GetCadastreData")
            .WithSummary("Vrátí uložená katastrální data pro inzerát");

        // ── RUIAN fetch ───────────────────────────────────────────────────────
        group.MapPost("/listings/{listingId:guid}/fetch", FetchCadastreData)
            .WithName("FetchCadastreData")
            .WithSummary("Spustí RUIAN vyhledávání pro jeden inzerát a výsledek uloží do DB");

        // ── Manuální data ─────────────────────────────────────────────────────
        group.MapPut("/listings/{listingId:guid}", SaveManualData)
            .WithName("SaveCadastreManualData")
            .WithSummary("Uloží manuálně zadaná katastrální data (LV, břemena, výměra)");

        // ── Hromadný fetch ────────────────────────────────────────────────────
        group.MapPost("/bulk-fetch", BulkFetch)
            .WithName("BulkFetchCadastre")
            .WithSummary("Spustí hromadné RUIAN vyhledávání pro inzeráty bez katastrálních dat");

        return app;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<Results<Ok<ListingCadastreDto>, NotFound<string>>> GetCadastreData(
        Guid listingId,
        ICadastreService service,
        CancellationToken ct)
    {
        var data = await service.GetAsync(listingId, ct);
        if (data is null)
            return TypedResults.NotFound($"Katastrální data pro inzerát {listingId} nenalezena.");
        return TypedResults.Ok(data);
    }

    private static async Task<Results<Ok<ListingCadastreDto>, NotFound<string>, BadRequest<string>>> FetchCadastreData(
        Guid listingId,
        ICadastreService service,
        CancellationToken ct)
    {
        try
        {
            var data = await service.FetchAndSaveAsync(listingId, ct);
            return TypedResults.Ok(data);
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return TypedResults.BadRequest($"RUIAN fetch selhal: {ex.Message}");
        }
    }

    private static async Task<Results<Ok<ListingCadastreDto>, NotFound<string>>> SaveManualData(
        Guid listingId,
        [FromBody] SaveCadastreDataRequest request,
        ICadastreService service,
        CancellationToken ct)
    {
        try
        {
            var data = await service.SaveManualDataAsync(listingId, request, ct);
            return TypedResults.Ok(data);
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    private static async Task<Ok<BulkRuianResultDto>> BulkFetch(
        [FromQuery] int batchSize,
        ICadastreService service,
        CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 200);
        var result = await service.BulkFetchAsync(batchSize, ct);
        return TypedResults.Ok(result);
    }
}
