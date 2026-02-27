using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RealEstate.Api.Contracts.UserPhotos;
using RealEstate.Api.Services;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/listings");

        group.MapPost("/{id:guid}/export-drive", ExportToDrive)
            .WithName("ExportListingToDrive")
            .WithTags("Export");

        group.MapPost("/{id:guid}/export-onedrive", ExportToOneDrive)
            .WithName("ExportListingToOneDrive")
            .WithTags("Export");

        // Vrátí AI instrukce jako plain text – bez OneDrive, ready-to-paste do Claude/Perplexity
        group.MapGet("/{id:guid}/ai-brief", GetAiBrief)
            .WithName("GetAiBrief")
            .WithTags("Export");

        // Uloží analýzu (plain text) do OneDrive složky jako ANALYZA_datum.md
        group.MapPost("/{id:guid}/save-analysis", SaveAnalysis)
            .WithName("SaveAnalysis")
            .WithTags("Export");

        // Vrátí stav exportu (folder IDs) z DB – pro obnovení UI po refreshi/crashu
        group.MapGet("/{id:guid}/export-state", GetExportState)
            .WithName("GetExportState")
            .WithTags("Export");

        // Nahraje fotky z prohlídky do podsložky Moje_fotky_z_prohlidky (Drive nebo OneDrive)
        // + uloží lokální kopii do uploads/listings/{id}/inspection/ pro MCP/AI analýzu
        group.MapPost("/{id:guid}/upload-inspection-photos", UploadInspectionPhotos)
            .WithName("UploadInspectionPhotos")
            .WithTags("Export")
            .DisableAntiforgery();

        // Vrátí seznam lokálně uložených fotek z prohlídky (pro MCP/AI analýzu)
        group.MapGet("/{id:guid}/inspection-photos", GetInspectionPhotos)
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

        return app;
    }

    private static async Task<IResult> SaveAnalysis(
        Guid id,
        [FromQuery] string folderId,
        [FromServices] IOneDriveExportService oneDriveService,
        HttpRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return Results.BadRequest(new { error = "Parametr folderId je povinný." });

        string content;
        using (var reader = new System.IO.StreamReader(req.Body))
            content = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(content))
            return Results.BadRequest(new { error = "Tělo požadavku (text analýzy) je prázdné." });

        try
        {
            await oneDriveService.SaveAnalysisAsync(folderId, content, ct);
            return Results.Ok(new { status = "saved", message = "Analýza uložena do OneDrive složky." });
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
        CancellationToken ct)
    {
        var listing = await db.Listings
            .Include(l => l.Photos)
            .Include(l => l.Source)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null)
            return Results.NotFound(new { error = $"Inzerát {id} nenalezen" });

        var photos = listing.Photos
            .OrderBy(p => p.Order)
            .Take(20)
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

    private static async Task<IResult> ExportToDrive(
        Guid id,
        [FromServices] IGoogleDriveExportService exportService,
        CancellationToken ct)
    {
        try
        {
            var result = await exportService.ExportListingToDriveAsync(id, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Chyba při exportu na Google Drive",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ExportToOneDrive(
        Guid id,
        [FromServices] IOneDriveExportService exportService,
        CancellationToken ct)
    {
        try
        {
            var result = await exportService.ExportListingAsync(id, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: "Chyba při exportu na OneDrive",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
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
                l.DriveInspectionFolderId,
                l.OneDriveFolderId,
                l.OneDriveInspectionFolderId
            })
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null) return Results.NotFound();

        return Results.Ok(new
        {
            driveFolderId       = listing.DriveFolderId,
            driveFolderUrl      = listing.DriveFolderId is not null
                ? $"https://drive.google.com/drive/folders/{listing.DriveFolderId}"
                : null,
            driveInspectionFolderId    = listing.DriveInspectionFolderId,
            oneDriveFolderId           = listing.OneDriveFolderId,
            oneDriveInspectionFolderId = listing.OneDriveInspectionFolderId
        });
    }

    private static async Task<IResult> UploadInspectionPhotos(
        Guid id,
        [FromQuery] string provider,
        [FromServices] IGoogleDriveExportService driveService,
        [FromServices] IOneDriveExportService oneDriveService,
        [FromServices] RealEstateDbContext db,
        [FromServices] IWebHostEnvironment env,
        HttpRequest req,
        CancellationToken ct)
    {
        // Folder ID bereme z DB – není potřeba session state
        var listing = await db.Listings
            .AsNoTracking()
            .Select(l => new { l.Id, l.DriveInspectionFolderId, l.OneDriveInspectionFolderId })
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing is null) return Results.NotFound(new { error = "Inzerát nenalezen." });

        var isOneDrive = provider?.Equals("onedrive", StringComparison.OrdinalIgnoreCase) == true;
        var inspectionFolderId = isOneDrive
            ? listing.OneDriveInspectionFolderId
            : listing.DriveInspectionFolderId;

        if (string.IsNullOrWhiteSpace(inspectionFolderId))
            return Results.BadRequest(new { error = $"Inzerát nebyl exportován na {(isOneDrive ? "OneDrive" : "Google Drive")}. Nejprve proveďte export." });

        if (!req.HasFormContentType)
            return Results.BadRequest(new { error = "Požadavek musí být multipart/form-data." });

        var form = await req.ReadFormAsync(ct);
        if (form.Files.Count == 0)
            return Results.BadRequest(new { error = "Žádné soubory k nahrání." });

        var files = new List<(string Name, byte[] Data, string ContentType)>();
        for (int i = 0; i < form.Files.Count; i++)
        {
            var file = form.Files[i];
            using var ms = new System.IO.MemoryStream();
            await file.CopyToAsync(ms, ct);
            var safeName = Path.GetFileName(file.FileName);
            var ct2 = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
            files.Add(($"prohlidka_{i + 1:D2}_{safeName}", ms.ToArray(), ct2));
        }

        try
        {
            if (isOneDrive)
                await oneDriveService.UploadInspectionPhotosAsync(inspectionFolderId, files, ct);
            else
                await driveService.UploadInspectionPhotosAsync(inspectionFolderId, files, ct);

            // ── Lokální kopie pro MCP/AI analýzu ──────────────────────────────
            var photosBaseUrl = Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
                ?? $"{req.Scheme}://{req.Host}";
            var inspDir = Path.Combine(env.WebRootPath, "uploads", "listings", id.ToString(), "inspection");
            Directory.CreateDirectory(inspDir);

            // Smažeme staré lokální kopie před uložením nových
            foreach (var old in Directory.GetFiles(inspDir))
                File.Delete(old);

            var existingRecords = await db.UserListingPhotos
                .Where(p => p.ListingId == id)
                .ToListAsync(ct);
            db.UserListingPhotos.RemoveRange(existingRecords);

            var now = DateTime.UtcNow;
            for (int i = 0; i < files.Count; i++)
            {
                var (name, data, _) = files[i];
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var fileName = $"{i:D3}_{Path.GetFileNameWithoutExtension(name)}{ext}";
                var fullPath = Path.Combine(inspDir, fileName);
                await File.WriteAllBytesAsync(fullPath, data, ct);

                var relUrl = $"{photosBaseUrl}/uploads/listings/{id}/inspection/{fileName}";
                db.UserListingPhotos.Add(new UserListingPhoto
                {
                    Id = Guid.NewGuid(),
                    ListingId = id,
                    StoredUrl = relUrl,
                    OriginalFileName = name,
                    FileSizeBytes = data.Length,
                    TakenAt = now,
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

    private static async Task<IResult> GetInspectionPhotos(
        Guid id,
        [FromServices] RealEstateDbContext db,
        [FromServices] IStorageService storageService,
        CancellationToken ct)
    {
        var photos = await db.UserListingPhotos
            .Where(p => p.ListingId == id)
            .OrderBy(p => p.UploadedAt)
            .ToListAsync(ct);

        var dtos = new List<UserListingPhotoDto>();
        foreach (var photo in photos)
        {
            var publicUrl = await storageService.GetFileUrlAsync(photo.StoredUrl, ct);
            
            dtos.Add(new UserListingPhotoDto(
                photo.Id,
                publicUrl ?? photo.StoredUrl,
                photo.OriginalFileName,
                photo.FileSizeBytes,
                photo.TakenAt,
                photo.UploadedAt,
                photo.Notes,
                photo.AiDescription
            ));
        }

        return Results.Ok(dtos);
    }

    private static async Task<IResult> SaveInspectionPhotoAiDescription(
        Guid id,
        Guid photoId,
        [FromBody] SavePhotoAiDescriptionRequest req,
        [FromServices] RealEstateDbContext db,
        CancellationToken ct)
    {
        var photo = await db.UserListingPhotos
            .FirstOrDefaultAsync(p => p.Id == photoId && p.ListingId == id, ct);
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
