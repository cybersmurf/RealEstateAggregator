using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Services;
using RealEstate.Infrastructure;

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

        // ── Zpětná vazba na klasifikaci ───────────────────────────────────
        group.MapPatch("/{photoId:guid}/classification-feedback", SaveClassificationFeedback)
            .WithName("SaveClassificationFeedback")
            .WithSummary("Uloží zpětnou vazbu uživatele na Ollama klasifikaci fotky: correct | wrong | null (odvolat).")
            .Produces(200)
            .Produces(404);

        // ── Řazení fotek dle kategorie ────────────────────────────────────
        group.MapPost("/sort-by-category", SortByCategory)
            .WithName("SortPhotosByCategory")
            .WithSummary("Seřadí fotky inzerátu dle priority kategorie: exteriér → obývák → kuchyň → koupelna → ložnice → ...")
            .Produces<PhotoSortResultDto>(200)
            .Produces(400);

        // ── Accessibility alt text (WCAG 2.2 AA) ─────────────────────────
        group.MapPost("/bulk-alt-text", BulkAltText)
            .WithName("BulkAltText")
            .WithSummary("Generuje accessibility alt text pro dávku fotek přes Ollama Vision (WCAG 2.2 AA).")
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

    private record ClassificationFeedbackRequest(string? Feedback);

    private static async Task<IResult> SaveClassificationFeedback(
        Guid photoId,
        [FromBody] ClassificationFeedbackRequest request,
        [FromServices] RealEstateDbContext db,
        CancellationToken ct)
    {
        if (request.Feedback is not null
            && request.Feedback != "correct"
            && request.Feedback != "wrong")
        {
            return Results.Problem(
                title: "Neplatný feedback",
                detail: "Hodnota musí být 'correct', 'wrong' nebo null (odvolání).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var photo = await db.ListingPhotos.FindAsync([photoId], ct);
        if (photo is null)
            return Results.NotFound(new { message = $"Fotka {photoId} nenalezena." });

        photo.ClassificationFeedback = request.Feedback;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id = photoId,
            feedback = request.Feedback,
            category = photo.PhotoCategory,
            damageDetected = photo.DamageDetected
        });
    }

    private static async Task<IResult> SortByCategory(
        [FromQuery] Guid listingId,
        [FromServices] IPhotoClassificationService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (listingId == Guid.Empty)
            return Results.Problem(
                title: "Chybí listingId",
                detail: "Parametr listingId je povinný.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.SortByCategoryAsync(listingId, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> BulkAltText(
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

        var result = await service.BulkAltTextAsync(batchSize, cancellationToken, listingId);
        return Results.Ok(result);
    }
}
