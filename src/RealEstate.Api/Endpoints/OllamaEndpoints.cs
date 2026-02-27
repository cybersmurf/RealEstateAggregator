using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class OllamaEndpoints
{
    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ollama")
            .WithTags("Ollama Text");

        // ── Smart Tags ──────────────────────────────────────────────────────────
        group.MapPost("/bulk-smart-tags", BulkSmartTags)
            .WithName("BulkSmartTags")
            .WithSummary("Generuje 5 smart tagů z popisu inzerátu (llama3.2 text).")
            .Produces<OllamaTextBatchResultDto>(200);

        // ── Description normalization ───────────────────────────────────────────
        group.MapPost("/bulk-normalize", BulkNormalize)
            .WithName("BulkNormalize")
            .WithSummary("Extrahuje strukturovaná data z popisu: rok stavby, patro, výtah, sklep, zahrada... (llama3.2 text).")
            .Produces<OllamaTextBatchResultDto>(200);

        // ── Price opinion ───────────────────────────────────────────────────────
        group.MapPost("/bulk-price-opinion", BulkPriceOpinion)
            .WithName("BulkPriceOpinion")
            .WithSummary("Generuje cenový signál low/fair/high na základě lokality, plochy a stavu (llama3.2 text).")
            .Produces<OllamaTextBatchResultDto>(200);

        // ── Duplicate detection ─────────────────────────────────────────────────
        group.MapPost("/detect-duplicates", DetectDuplicates)
            .WithName("DetectDuplicates")
            .WithSummary("Porovná dva inzeráty a vyhodnotí, zda jde o tutéž nemovitost (llama3.2 text).")
            .Produces<DuplicateDetectionResultDto>(200)
            .Produces(400)
            .Produces(404);

        // ── Stats ───────────────────────────────────────────────────────────────
        group.MapGet("/stats", GetStats)
            .WithName("OllamaTextStats")
            .WithSummary("Statistika zpracovaných inzerátů pro každou Ollama text funkci.")
            .Produces<OllamaTextStatsDto>(200);

        return app;
    }

    private static async Task<IResult> BulkSmartTags(
        [FromQuery] int batchSize = 20,
        [FromServices] IOllamaTextService service = default!,
        CancellationToken ct = default)
    {
        if (batchSize < 1 || batchSize > 50)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.BulkSmartTagsAsync(batchSize, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkNormalize(
        [FromQuery] int batchSize = 20,
        [FromServices] IOllamaTextService service = default!,
        CancellationToken ct = default)
    {
        if (batchSize < 1 || batchSize > 50)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.BulkNormalizeAsync(batchSize, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkPriceOpinion(
        [FromQuery] int batchSize = 20,
        [FromServices] IOllamaTextService service = default!,
        CancellationToken ct = default)
    {
        if (batchSize < 1 || batchSize > 50)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.BulkPriceOpinionAsync(batchSize, ct);
        return Results.Ok(result);
    }

    private record DetectDuplicatesRequest(Guid ListingId1, Guid ListingId2);

    private static async Task<IResult> DetectDuplicates(
        [FromBody] DetectDuplicatesRequest request,
        [FromServices] IOllamaTextService service = default!,
        CancellationToken ct = default)
    {
        if (request.ListingId1 == request.ListingId2)
            return Results.Problem(
                title: "Stejné ID",
                detail: "ListingId1 a ListingId2 musí být různá.",
                statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var result = await service.DetectDuplicatesAsync(request.ListingId1, request.ListingId2, ct);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetStats(
        [FromServices] IOllamaTextService service = default!,
        CancellationToken ct = default)
    {
        var stats = await service.GetStatsAsync(ct);
        return Results.Ok(stats);
    }
}
