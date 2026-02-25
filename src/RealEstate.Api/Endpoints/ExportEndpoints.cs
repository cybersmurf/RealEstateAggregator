using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Services;
using RealEstate.Infrastructure;

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
        group.MapPost("/{id:guid}/upload-inspection-photos", UploadInspectionPhotos)
            .WithName("UploadInspectionPhotos")
            .WithTags("Export")
            .DisableAntiforgery();

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

            return Results.Ok(new { uploaded = files.Count, message = $"Nahráno {files.Count} fotek z prohlídky." });
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Chyba při nahrávání fotek z prohlídky", detail: ex.Message, statusCode: 500);
        }
    }}