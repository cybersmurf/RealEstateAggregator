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

        // ── OCR screenshot ────────────────────────────────────────────────────
        group.MapPost("/listings/{listingId:guid}/ocr-screenshot", OcrScreenshot)
            .WithName("OcrCadastreScreenshot")
            .WithSummary("OCR screenshot z nahlíženídokn.cuzk.cz – extrahuje parcelní číslo, LV, výměru, břemena přes Ollama Vision")
            .DisableAntiforgery()
            .Produces<CadastreOcrResultDto>(200)
            .Produces(400)
            .Produces(500);

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

    private static async Task<IResult> OcrScreenshot(
        Guid listingId,
        IFormFile file,
        ICadastreService service,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest("Soubor nebyl odeslán nebo je prázdný.");

        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Soubor musí být obrázek (image/*).");

        if (file.Length > 20 * 1024 * 1024)   // 20 MB limit
            return Results.BadRequest("Soubor je příliš velký (max 20 MB).");

        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var imageData = ms.ToArray();

            var result = await service.OcrScreenshotAsync(listingId, imageData, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return Results.Problem($"OCR zpracování selhalo: {ex.Message}");
        }
    }
}
