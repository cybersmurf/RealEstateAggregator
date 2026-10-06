using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Endpoints;

public static class PhotoEndpoints
{
    public static IEndpointRouteBuilder MapPhotoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/photos")
            .WithTags("Photos")
            .RequireAdmin();

        group.MapPost("/bulk-download", BulkDownload)
            .WithName("BulkDownloadPhotos")
            .WithSummary("Stáhne dávku fotek z original_url a uloží je lokálně.")
            .Produces<PhotoDownloadResultDto>(200);

        group.MapGet("/stats", GetStats)
            .WithName("GetPhotoStats")
            .WithSummary("Vrátí statistiku stažených vs. nestažených fotek.")
            .Produces<PhotoDownloadStatsDto>(200);

        group.MapPost("/detect-twins", DetectTwins)
            .WithName("DetectPhotoTwins")
            .WithSummary("Najde v galerii inzerátu dvojice fotek se stejným záběrem a jiným interiérem (retuš, vizualizace).")
            .Produces<PhotoTwinResultDto>(200);

        group.MapPost("/detect-house-position", DetectHousePosition)
            .WithName("DetectHousePosition")
            .WithSummary("Určí z venkovních a leteckých fotek polohu domu vůči sousedům (samostatný / přisazený / řadový / rohový).")
            .Produces<HousePositionResultDto>(200);

        // ── Mazání lokálních kopií (fotky zdrojů nesmí zůstat na veřejném webu) ──
        group.MapPost("/purge-stored", PurgeStored)
            .WithName("PurgeStoredPhotos")
            .WithSummary("Smaže lokální kopie fotek inzerátů a vynuluje stored_url. Fotky z prohlídky se nemažou.")
            .Produces<PhotoPurgeResultDto>(200)
            .Produces(400);

        // ── Mistral Vision klasifikace ────────────────────────────────────
        group.MapPost("/bulk-classify", BulkClassify)
            .WithName("BulkClassifyPhotos")
            .WithSummary("Klasifikuje dávku fotek přes Mistral Vision. Vyžaduje stažené fotky (stored_url != null).")
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
        [FromQuery] bool onlyMyListings = false,
        [FromServices] IPhotoDownloadService service = default!,
        [FromServices] RealEstate.Api.Services.Duplicates.IDuplicateGroupService groups = default!,
        CancellationToken cancellationToken = default)
    {
        listingId = await ResolvePhotoOwnerAsync(groups, listingId, cancellationToken);
        if (batchSize < 1 || batchSize > 200)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–200.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.DownloadBatchAsync(batchSize, cancellationToken, listingId, onlyMyListings);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetStats(
        [FromServices] IPhotoDownloadService service = default!,
        CancellationToken cancellationToken = default)
    {
        var stats = await service.GetStatsAsync(cancellationToken);
        return Results.Ok(stats);
    }

    private static async Task<IResult> PurgeStored(
        [FromQuery] bool onlyClassified = true,
        [FromQuery] int olderThanDays = 0,
        [FromQuery] int batchSize = 500,
        [FromServices] IPhotoPurgeService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 5000)
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–5000.",
                statusCode: StatusCodes.Status400BadRequest);

        if (olderThanDays < 0)
            return Results.Problem(
                title: "Neplatný olderThanDays",
                detail: "olderThanDays nesmí být záporné (0 = bez omezení stáří).",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.PurgeStoredAsync(onlyClassified, olderThanDays, batchSize, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> DetectHousePosition(
        [FromQuery] Guid listingId,
        [FromQuery] bool wait = true,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs = default!,
        CancellationToken cancellationToken = default)
    {
        var jobId = jobs.Enqueue("house-position", listingId, async (sp, ct) =>
            await sp.GetRequiredService<IHousePositionService>().DetectAsync(listingId, ct));
        if (!wait)
            return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
        var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(20), cancellationToken);
        return Results.Ok(job.Result);
    }

    private static async Task<IResult> DetectTwins(
        [FromQuery] Guid listingId,
        [FromQuery] bool wait = true,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs = default!,
        CancellationToken cancellationToken = default)
    {
        var jobId = jobs.Enqueue("photo-twins", listingId, async (sp, ct) =>
            await sp.GetRequiredService<IPhotoTwinService>().DetectAsync(listingId, ct));
        if (!wait)
            return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
        var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(20), cancellationToken);
        return Results.Ok(job.Result);
    }

    private static async Task<IResult> BulkClassify(
        [FromQuery] int batchSize = 20,
        [FromQuery] Guid? listingId = null,
        [FromQuery] bool onlyMyListings = false,
        [FromQuery] bool wait = true,
        [FromServices] IPhotoClassificationService service = default!,
        [FromServices] RealEstate.Api.Services.Duplicates.IDuplicateGroupService groups = default!,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs = default!,
        CancellationToken cancellationToken = default)
    {
        // Úloha se eviduje pod inzerátem, který uživatel otevřel – detail se na ni ptá svým Id,
        // i když fotky patří jinému členovi skupiny duplicit (vlastník s nejvíce fotkami).
        var requestedListingId = listingId;
        listingId = await ResolvePhotoOwnerAsync(groups, listingId, cancellationToken);
        // Validace batchSize jen pro globální bulk (bez listingId).
        // Když je listingId zadáno, service zpracuje VŠECHNY fotky listingu bez omezení.
        if (!listingId.HasValue && (batchSize < 1 || batchSize > 50))
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–50 (Vision model je pomalý).",
                statusCode: StatusCodes.Status400BadRequest);

        // Běží jako úloha na pozadí – odchod ze stránky (zrušený požadavek) klasifikaci nezastaví.
        var jobId = jobs.Enqueue("photo-classify", requestedListingId, async (sp, ct) =>
        {
            var result = await sp.GetRequiredService<IPhotoClassificationService>().ClassifyBatchAsync(batchSize, ct, listingId, onlyMyListings);
            // Po klasifikaci celé galerie rovnou „dvojčata" – stejný záběr, jiný interiér (retuš, vizualizace)
            if (listingId.HasValue && result.Error is null && result.Succeeded > 0)
            {
                await sp.GetRequiredService<IPhotoTwinService>().DetectAsync(listingId.Value, ct);
                // … a polohu domu vůči sousedům – venkovní fotky se mohly změnit
                await sp.GetRequiredService<IHousePositionService>().DetectAsync(requestedListingId ?? listingId.Value, ct);
            }
            return result;
        });
        if (!wait)
            return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
        var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(45), cancellationToken);
        return Results.Ok(job.Result);
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
        [FromQuery] bool onlyMyListings = false,
        [FromServices] IPhotoClassificationService service = default!,
        CancellationToken cancellationToken = default)
    {
        if (!listingId.HasValue && (batchSize < 1 || batchSize > 50))
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var result = await service.ClassifyInspectionBatchAsync(batchSize, cancellationToken, listingId, onlyMyListings);
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
        [FromServices] RealEstate.Api.Services.Duplicates.IDuplicateGroupService groups = default!,
        CancellationToken cancellationToken = default)
    {
        if (listingId != Guid.Empty)
            listingId = (await ResolvePhotoOwnerAsync(groups, listingId, cancellationToken))!.Value;
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
        [FromQuery] bool wait = true,
        [FromServices] IPhotoClassificationService service = default!,
        [FromServices] RealEstate.Api.Services.Duplicates.IDuplicateGroupService groups = default!,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs = default!,
        CancellationToken cancellationToken = default)
    {
        var requestedListingId = listingId;
        listingId = await ResolvePhotoOwnerAsync(groups, listingId, cancellationToken);
        if (!listingId.HasValue && (batchSize < 1 || batchSize > 50))
            return Results.Problem(
                title: "Neplatný batchSize",
                detail: "batchSize musí být v rozmezí 1–50.",
                statusCode: StatusCodes.Status400BadRequest);

        var jobId = jobs.Enqueue("photo-alt-text", requestedListingId, async (sp, ct) =>
            await sp.GetRequiredService<IPhotoClassificationService>().BulkAltTextAsync(batchSize, ct, listingId));
        if (!wait)
            return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
        var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(45), cancellationToken);
        return Results.Ok(job.Result);
    }

    /// <summary>
    /// Fotky se klasifikují/stahují u člena skupiny duplicit s nejúplnější sadou (Bazoš 20 vs. Sreality 49),
    /// stejně jako je detail zobrazuje. Bez skupiny vrací totéž ID.
    /// </summary>
    private static async Task<Guid?> ResolvePhotoOwnerAsync(
        RealEstate.Api.Services.Duplicates.IDuplicateGroupService groups, Guid? listingId, CancellationToken ct)
    {
        if (listingId is null || listingId == Guid.Empty) return listingId;
        try
        {
            return (await groups.GetGroupPhotoSetAsync(listingId.Value, ct)).OwnerListingId;
        }
        catch (KeyNotFoundException)
        {
            return listingId;
        }
    }
}
