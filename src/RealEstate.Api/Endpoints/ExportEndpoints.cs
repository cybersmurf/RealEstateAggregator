using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RealEstate.Api.Contracts.UserPhotos;
using RealEstate.Api.Services;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

using RealEstate.Api.Helpers;

namespace RealEstate.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        // Export na Drive, fotky z prohlídky, AI brief – nástroje vlastníka
        var group = app.MapGroup("/api/listings").RequireAdmin();

        group.MapPost("/{id:guid}/export-drive", ExportToDrive)
            .WithName("ExportListingToDrive")
            .WithTags("Export");

        // Vrátí AI instrukce jako plain text – ready-to-paste do Claude/Perplexity
        group.MapGet("/{id:guid}/ai-brief", GetAiBrief)
            .WithName("GetAiBrief")
            .WithTags("Export");

        // Uloží analýzu (plain text) do Google Drive složky jako ANALYZA_datum.md
        group.MapPost("/{id:guid}/save-analysis", SaveAnalysis)
            .WithName("SaveAnalysis")
            .WithTags("Export");

        // Vrátí stav exportu (folder IDs) z DB – pro obnovení UI po refreshi/crashu
        group.MapGet("/{id:guid}/export-state", GetExportState)
            .WithName("GetExportState")
            .WithTags("Export");

        // Nahraje fotky z prohlídky do podsložky Moje_fotky_z_prohlidky na Google Drive
        // + uloží lokální kopii do uploads/listings/{id}/inspection/ pro MCP/AI analýzu
        group.MapPost("/{id:guid}/upload-inspection-photos", UploadInspectionPhotos)
            .WithName("UploadInspectionPhotos")
            .WithTags("Export")
            .DisableAntiforgery();

        // Vrátí seznam lokálně uložených fotek z prohlídky (pro MCP/AI analýzu)
        // Čtení fotek z prohlídky smí i člen společného prostoru – proto mimo skupinu jen pro správce
        app.MapGroup("/api/listings").RequireInspectionRecords()
            .MapGet("/{id:guid}/inspection-photos", GetInspectionPhotos)
            .WithName("GetInspectionPhotos")
            .WithTags("Export");

        // Uloží AI popis k fotce z prohlídky
        group.MapPatch("/{id:guid}/inspection-photos/{photoId:guid}/ai-description", SaveInspectionPhotoAiDescription)
            .WithName("SaveInspectionPhotoAiDescription")
            .WithTags("Export");

        // Uloží AI popis k fotce z inzerátu
        group.MapPatch("/{id:guid}/photos/{photoId:guid}/ai-description", SaveListingPhotoAiDescription)
            .WithName("SaveListingPhotoAiDescription")
            .WithTags("Export");

        // Rescan GD Moje_fotky_z_prohlidky → delta import nových fotek do DB
        group.MapPost("/{id:guid}/scan-drive-inspection", ScanDriveInspection)
            .WithName("ScanDriveInspection")
            .WithSummary("Prohledá GD složku s fotkami z prohlídky a importuje nové soubory lokálně + do user_listing_photos.")
            .WithTags("Export");

        // Vrátí seznam souborů v GD složce, jejichž název obsahuje "analyz"
        // Fotky od makléře z Drive podsložky Fotky_od_maklere (kategorie + popisy z FOTKY_OD_MAKLERE.md)
        group.MapGet("/{id:guid}/broker-photos", GetBrokerPhotos)
            .WithName("GetBrokerPhotos")
            .WithTags("Export");

        group.MapGet("/{id:guid}/drive-analysis-files", ListDriveAnalysisFiles)
            .WithName("ListDriveAnalysisFiles")
            .WithTags("Export");

        // Exportuje analýzu z DB na Google Drive jako ANALYZA_datum.md
        group.MapPost("/{id:guid}/export-analysis-to-drive", ExportAnalysisToDrive)
            .WithName("ExportAnalysisToDrive")
            .WithSummary("Nahraje uloženou analýzu z DB do Google Drive složky inzerátu.")
            .WithTags("Export");

        return app;
    }

    private static async Task<IResult> ListDriveAnalysisFiles(
        Guid id,
        [FromServices] IGoogleDriveExportService driveService,
        CancellationToken ct)
    {
        var files = await driveService.ListAnalysisFilesAsync(id, ct);
        return Results.Ok(files);
    }

    private static async Task<IResult> SaveAnalysis(
        Guid id,
        [FromQuery] string? title,
        [FromServices] IGoogleDriveExportService driveService,
        HttpRequest req,
        CancellationToken ct)
    {
        string content;
        using (var reader = new System.IO.StreamReader(req.Body))
            content = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(content))
            return Results.BadRequest(new { error = "Tělo požadavku (text analýzy) je prázdné." });

        try
        {
            var fileUrl = await driveService.SaveAnalysisAsync(id, content, title ?? "analyza", ct);
            return Results.Ok(new { status = "saved", url = fileUrl, message = "Analýza uložena do Google Drive složky." });
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Chyba při ukládání analýzy", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> GetAiBrief(
        Guid id,
        [FromQuery] string? folderUrl,
        [FromServices] RealEstateDbContext db,
        [FromServices] RealEstate.Api.Services.Duplicates.IDuplicateGroupService duplicateGroups,
        CancellationToken ct)
    {
        var listing = await db.Listings
            .Include(l => l.Source)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null)
            return Results.NotFound(new { error = $"Inzerát {id} nenalezen" });

        listing.Photos = (await duplicateGroups.GetGroupPhotoSetAsync(id, ct)).Photos.ToList();

        var photos = listing.Photos
            .OrderBy(p => p.Order)
            .Take(50)
            .Where(p => !string.IsNullOrWhiteSpace(p.OriginalUrl))
            .Select((p, i) =>
            {
                var ext = Path.GetExtension(p.OriginalUrl!.Split('?')[0]).ToLowerInvariant();
                var name = $"foto_{i + 1:D2}{(ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".jpg")}";
                return new PhotoLink(name, p.OriginalUrl!, p.OriginalUrl!);
            })
            .ToList();

        var markdown = ListingExportContentBuilder.BuildAiInstructions(listing, photos, folderUrl);
        return Results.Text(markdown, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> ExportAnalysisToDrive(
        Guid id,
        [FromQuery] Guid? analysisId,
        [FromQuery] bool? wait,
        [FromServices] IGoogleDriveExportService driveService,
        [FromServices] RealEstateDbContext db,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs,
        CancellationToken ct)
    {
        // Načteme analýzu z DB – buď konkrétní (dle analysisId) nebo poslední non-auto
        var query = db.ListingAnalyses
            .AsNoTracking()
            .Where(a => a.ListingId == id);

        var analysis = analysisId.HasValue
            ? await query.FirstOrDefaultAsync(a => a.Id == analysisId.Value, ct)
            : await query
                .Where(a => a.Source != "auto")
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

        if (analysis is null)
            return Results.NotFound(new { error = "Žádná analýza nenalezena. Nejprve spusťte analyze-local nebo uložte analýzu ručně." });

        try
        {
            var title = analysis.Title ?? $"Analýza_{id}";
            var content = analysis.Content;
            var chosenId = analysis.Id;
            var source = analysis.Source;
            var jobId = jobs.Enqueue("drive-analysis-export", id, async (sp, token) =>
            {
                var fileUrl = await sp.GetRequiredService<IGoogleDriveExportService>().SaveAnalysisAsync(id, content, title, token);
                return new { fileUrl, analysisId = chosenId, title, source };
            });
            if (wait == false)
                return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
            var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(30), ct);
            return Results.Ok(job.Result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Chyba při nahrávání analýzy na Google Drive", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> ExportToDrive(
        Guid id,
        [FromQuery] bool? wait,
        [FromServices] IGoogleDriveExportService exportService,
        [FromServices] RealEstate.Api.Services.Jobs.IBackgroundJobService jobs,
        CancellationToken ct)
    {
        try
        {
            // Úloha na pozadí: zavření stránky export nepřeruší (dřív zrušený požadavek nechal
            // rozdělanou složku na Drive). ?wait=false vrátí 202 + jobId, výchozí čeká jako dřív.
            var jobId = jobs.Enqueue("drive-export", id, async (sp, token) =>
                await sp.GetRequiredService<IGoogleDriveExportService>().ExportListingToDriveAsync(id, token));
            if (wait == false)
                return Results.Accepted($"/api/jobs/{jobId}", new { jobId });
            var job = await jobs.WaitAsync(jobId, TimeSpan.FromMinutes(30), ct);
            return Results.Ok(job.Result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (GoogleDriveAuthException ex)
        {
            return Results.Problem(
                title: "Google Drive není autorizován",
                detail: ex.Message,
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>
                {
                    ["reauthorizeUrl"] = GoogleDriveAuthException.ReauthorizePath
                });
        }
        catch (Exception ex) when (GoogleDriveAuthHelper.IsInvalidGrant(ex))
        {
            return Results.Problem(
                title: "Google Drive token vypršel",
                detail: "Token has been expired or revoked. Znovu autorizuj Google účet.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?>
                {
                    ["reauthorizeUrl"] = GoogleDriveAuthException.ReauthorizePath
                });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Chyba při exportu na Google Drive",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetBrokerPhotos(
        Guid id,
        [FromServices] IGoogleDriveExportService driveService,
        [FromServices] ILogger<GoogleDriveExportService> logger,
        CancellationToken ct)
    {
        try
        {
            var photos = await driveService.ListBrokerPhotosAsync(id, ct);
            return photos is null ? Results.NoContent() : Results.Ok(photos);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nelze načíst fotky od makléře z Drive pro listing {Id}", id);
            return Results.NoContent();
        }
    }

    private static async Task<IResult> GetExportState(
        Guid id,
        [FromServices] RealEstateDbContext db,
        CancellationToken ct)
    {
        var listing = await db.Listings
            .AsNoTracking()
            .Select(l => new
            {
                l.Id,
                l.DriveFolderId,
                l.DriveInspectionFolderId
            })
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null) return Results.NotFound();

        return Results.Ok(new
        {
            driveFolderId       = listing.DriveFolderId,
            driveFolderUrl      = listing.DriveFolderId is not null
                ? $"https://drive.google.com/drive/folders/{listing.DriveFolderId}"
                : null,
            driveInspectionFolderId = listing.DriveInspectionFolderId
        });
    }

    private static async Task<IResult> UploadInspectionPhotos(
        Guid id,
        [FromServices] IGoogleDriveExportService driveService,
        [FromServices] RealEstateDbContext db,
        [FromServices] IWebHostEnvironment env,
        [FromServices] ILoggerFactory loggerFactory,
        HttpRequest req,
        CancellationToken ct)
    {
        // Folder ID bereme z DB – není potřeba session state
        var listing = await db.Listings
            .AsNoTracking()
            .Select(l => new { l.Id, l.DriveInspectionFolderId })
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null) return Results.NotFound(new { error = "Inzerát nenalezen." });

        var inspectionFolderId = listing.DriveInspectionFolderId;

        if (string.IsNullOrWhiteSpace(inspectionFolderId))
            return Results.BadRequest(new { error = "Inzerát nebyl exportován na Google Drive. Nejprve proveďte export." });

        if (!req.HasFormContentType)
            return Results.BadRequest(new { error = "Požadavek musí být multipart/form-data." });

        var form = await req.ReadFormAsync(ct);
        if (form.Files.Count == 0)
            return Results.BadRequest(new { error = "Žádné soubory k nahrání." });

        // Nové fotky se PŘIDÁVAJÍ za dosavadní – číslování pokračuje, aby se na Drivu ani na disku
        // nepotkaly dva soubory stejného jména. Dřív každé nahrání smazalo lokální kopie i záznamy
        // všech předchozích fotek inzerátu (zůstaly jen na Drivu).
        var inspDir = Path.Combine(env.WebRootPath, "uploads", "listings", id.ToString(), "inspection");
        Directory.CreateDirectory(inspDir);
        var startIndex = NextInspectionIndex(
            await db.UserListingPhotos.CountAsync(p => p.ListingId == id, ct),
            Directory.GetFiles(inspDir).Select(Path.GetFileName));

        var files = new List<(string Name, byte[] Data, string ContentType)>();
        for (int i = 0; i < form.Files.Count; i++)
        {
            var file = form.Files[i];
            using var ms = new System.IO.MemoryStream();
            await file.CopyToAsync(ms, ct);
            var safeName = Path.GetFileName(file.FileName);
            var ct2 = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
            var data = ms.ToArray();
            // HEIC z iPhonu → JPEG, jinak je fotka v galerii černá a pro obrazový model nepoužitelná
            if (Services.Photos.HeifConverter.IsHeif(safeName)
                && await Services.Photos.HeifConverter.ToJpegAsync(data, loggerFactory.CreateLogger("HeifConverter"), ct) is { } jpeg)
            {
                data = jpeg;
                safeName = Path.ChangeExtension(safeName, ".jpg");
                ct2 = "image/jpeg";
            }
            files.Add(($"prohlidka_{startIndex + i + 1:D2}_{safeName}", data, ct2));
        }

        try
        {
            await driveService.UploadInspectionPhotosAsync(inspectionFolderId, files, ct);

            // ── Lokální kopie pro MCP/AI analýzu ──────────────────────────────
            var now = DateTime.UtcNow;
            for (int i = 0; i < files.Count; i++)
            {
                var (name, data, _) = files[i];
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var fileName = $"{startIndex + i:D3}_{Path.GetFileNameWithoutExtension(name)}{ext}";
                var fullPath = Path.Combine(inspDir, fileName);
                await File.WriteAllBytesAsync(fullPath, data, ct);

                var relUrl = $"/uploads/listings/{id}/inspection/{fileName}";
                db.UserListingPhotos.Add(new UserListingPhoto
                {
                    Id = Guid.NewGuid(),
                    ListingId = id,
                    StoredUrl = relUrl,
                    OriginalFileName = name,
                    FileSizeBytes = data.Length,
                    TakenAt = Services.Photos.ExifReader.TakenAtUtc(data) ?? now,
                    UploadedAt = now
                });
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { uploaded = files.Count, message = $"Nahráno {files.Count} fotek z prohlídky." });
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Chyba při nahrávání fotek z prohlídky", detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>Inzerát a všechny jeho kopie ve skupině duplicit (včetně stažených).</summary>
    private static async Task<List<Guid>> InspectionGroupMemberIdsAsync(RealEstateDbContext db, Guid id, CancellationToken ct)
    {
        var rootId = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => l.DuplicateOfListingId ?? l.Id)
            .FirstOrDefaultAsync(ct);
        if (rootId == Guid.Empty) return [id];

        var ids = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == rootId || l.DuplicateOfListingId == rootId)
            .Select(l => l.Id)
            .ToListAsync(ct);
        if (!ids.Contains(id)) ids.Add(id);
        return ids;
    }

    /// <summary>
    /// První volné pořadové číslo pro nově nahrané fotky z prohlídky: za počtem záznamů
    /// i za nejvyšším číslem v názvech souborů na disku („071_prohlidka_72_IMG_6670.jpeg" → 72).
    /// </summary>
    public static int NextInspectionIndex(int existingRecords, IEnumerable<string?> existingFileNames)
    {
        var next = existingRecords;
        foreach (var name in existingFileNames)
        {
            var prefix = name?.Split('_', 2)[0];
            if (int.TryParse(prefix, out var index) && index + 1 > next)
                next = index + 1;
        }
        return next;
    }

    private static async Task<IResult> GetInspectionPhotos(
        Guid id,
        [FromServices] RealEstateDbContext db,
        [FromServices] IStorageService storageService,
        [FromServices] IPhotoClassificationService photoPaths,
        CancellationToken ct)
    {
        // Fotky z prohlídky patří domu: vrátíme je i u kopie, která se objevila až po prohlídce
        // (záznam visí na původním, mezitím staženém inzerátu ze stejné skupiny duplicit).
        var memberIds = await InspectionGroupMemberIdsAsync(db, id, ct);
        var photos = await db.UserListingPhotos
            .AsNoTracking()
            .Where(p => memberIds.Contains(p.ListingId))
            .OrderBy(p => p.TakenAt).ThenBy(p => p.OriginalFileName)
            .ToListAsync(ct);

        // Náhledy se vyrábějí při prvním čtení a zůstávají na disku vedle originálů (inspection/thumbs/)
        var thumbs = new System.Collections.Concurrent.ConcurrentDictionary<Guid, bool>();
        await Parallel.ForEachAsync(photos, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (photo, token) => thumbs[photo.Id] = await EnsureInspectionThumbnailAsync(photoPaths, photo.StoredUrl, token));

        var dtos = new List<UserListingPhotoDto>();
        foreach (var photo in photos)
        {
            var publicUrl = await storageService.GetFileUrlAsync(photo.StoredUrl, ct) ?? photo.StoredUrl;

            dtos.Add(new UserListingPhotoDto(
                photo.Id,
                publicUrl,
                photo.OriginalFileName,
                photo.FileSizeBytes,
                photo.TakenAt,
                photo.UploadedAt,
                photo.Notes,
                photo.AiDescription,
                thumbs.GetValueOrDefault(photo.Id) ? InspectionThumbnailUrl(publicUrl) : null,
                photo.PhotoCategory
            ));
        }

        return Results.Ok(dtos);
    }

    /// <summary>„…/inspection/012_IMG_7015.JPG" → „…/inspection/thumbs/012_IMG_7015.jpg".</summary>
    public static string InspectionThumbnailUrl(string photoUrl)
    {
        var slash = photoUrl.LastIndexOf('/');
        var name = Path.GetFileNameWithoutExtension(photoUrl[(slash + 1)..]);
        return $"{photoUrl[..slash]}/thumbs/{name}.jpg";
    }

    private static async Task<bool> EnsureInspectionThumbnailAsync(
        IPhotoClassificationService photoPaths, string storedUrl, CancellationToken ct)
    {
        try
        {
            var original = photoPaths.ResolveInspectionPhotoPath(storedUrl);
            var thumb = Path.Combine(Path.GetDirectoryName(original)!, "thumbs",
                Path.GetFileNameWithoutExtension(original) + ".jpg");
            if (File.Exists(thumb)) return true;
            if (!File.Exists(original)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
            var small = RealEstate.Api.Services.Vision.ImageDownscaler.ToJpeg(await File.ReadAllBytesAsync(original, ct), maxSide: 480);
            await File.WriteAllBytesAsync(thumb, small, ct);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<IResult> SaveInspectionPhotoAiDescription(
        Guid id,
        Guid photoId,
        [FromBody] SavePhotoAiDescriptionRequest req,
        [FromServices] RealEstateDbContext db,
        CancellationToken ct)
    {
        var memberIds = await InspectionGroupMemberIdsAsync(db, id, ct);
        var photo = await db.UserListingPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && memberIds.Contains(p.ListingId), ct);
        if (photo is null)
            return Results.NotFound();

        photo.AiDescription = req.Description;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveListingPhotoAiDescription(
        Guid id,
        Guid photoId,
        [FromBody] SavePhotoAiDescriptionRequest req,
        [FromServices] RealEstateDbContext db,
        CancellationToken ct)
    {
        var photo = await db.ListingPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.ListingId == id, ct);
        if (photo is null)
            return Results.NotFound();

        photo.AiDescription = req.Description;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult ScanDriveInspection(
        Guid id,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("GdScan");
        // Fire-and-forget s vlastním DI scope – DbContext nesmí sdílet scope s HTTP requestem
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IGoogleDriveExportService>();
            try
            {
                var result = await svc.ScanDriveInspectionFolderAsync(id, cts.Token);
                logger.LogInformation(
                    "GD scan background finished for {ListingId}: imported={Imported}, skipped={Skipped}, total={Total}",
                    id, result.Imported, result.Skipped, result.TotalInFolder);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GD scan background failed for {ListingId}", id);
            }
        });

        return Results.Accepted(value: new
        {
            message = "Scan GD složky spuštěn na pozadí. Fotky budou dostupné za 1–5 minut dle jejich počtu.",
            listingId = id
        });
    }
}

internal record SavePhotoAiDescriptionRequest(string Description);
