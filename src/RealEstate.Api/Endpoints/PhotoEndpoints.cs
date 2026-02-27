using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

public static class PhotoEndpoints
{
    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/photos")
            .WithTags("Photos");

        group.MapPost("/bulk-download", BulkDownload)
            .WithName("BulkDownloadPhotos")
            .WithSummary("Stáhne dávku fotek z original_url a uloží je lokálně.")
            .Produces<PhotoDownloadResultDto>(200);

        group.MapGet("/stats", GetStats)
            .WithName("GetPhotoStats")
            .WithSummary("Vrátí statistiku stažených vs. nestažených fotek.")
            .Produces<PhotoDownloadStatsDto>(200);

        // ── Ollama Vision klasifikace ─────────────────────────────────────
        group.MapPost("/bulk-classify", BulkClassify)
            .WithName("BulkClassifyPhotos")
            .WithSummary("Klasifikuje dávku fotek přes Ollama Vision (llama3.2-vision). Vyžaduje stažené fotky (stored_url != null).")
            .Produces<PhotoClassificationResultDto>(200);

        group.MapGet("/classification-stats", GetClassificationStats)
            .WithName("GetPhotoClassificationStats")
            .WithSummary("Vrátí statistiku klasifikovaných fotek a počet detekovaných poškození.")
            .Produces<PhotoClassificationStatsDto>(200);

        group.MapPost("/bulk-classify-inspection", BulkClassifyInspection)
            .WithName("BulkClassifyInspectionPhotos")
            .WithSummary("Klasifikuje dávku fotek z prohlídky (user_listing_photos) přes Ollama Vision.")
            .Produces<PhotoClassificationResultDto>(200);

        return app;
    }

    private static async Task<IResult> BulkDownload(
        [FromQuery] int batchSize = 50,
        [FromQuery] Guid? listingId = null,
        [FromServices] IPhotoDownloadService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 200)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–200.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.DownloadBatchAsync(batchSize, cancellationToken, listingId);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetStats(
        [FromServices] IPhotoDownloadService service = default!,
        CancellationToken cancellationToken = default)
    {
        var stats = await service.GetStatsAsync(cancellationToken);
        return Results.Ok(stats);
    }

    private static async Task<IResult> BulkClassify(
        [FromQuery] int batchSize = 20,
        [FromQuery] Guid? listingId = null,
        [FromServices] IPhotoClassificationService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 50)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–50 (Vision model je pomalý).",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.ClassifyBatchAsync(batchSize, cancellationToken, listingId);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetClassificationStats(
        [FromServices] IPhotoClassificationService service = default!,
        CancellationToken cancellationToken = default)
    {
        var stats = await service.GetClassificationStatsAsync(cancellationToken);
        return Results.Ok(stats);
    }

    private static async Task<IResult> BulkClassifyInspection(
        [FromQuery] int batchSize = 20,
        [FromQuery] Guid? listingId = null,
        [FromServices] IPhotoClassificationService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 50)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.ClassifyInspectionBatchAsync(batchSize, cancellationToken, listingId);
        return Results.Ok(result);
    }
}
