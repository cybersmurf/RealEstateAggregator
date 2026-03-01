using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Export;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace RealEstate.Api.Services;

public sealed class GoogleDriveExportService(
    RealEstateDbContext dbContext,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    ILogger<GoogleDriveExportService> logger) : IGoogleDriveExportService
{
    public async Task<DriveExportResultDto> ExportListingToDriveAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await dbContext.Listings
            .Include(l => l.Photos)
            .Include(l => l.Source)
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        // ── IDEMPOTENCE: pokud jsme už exportovali, vrátíme existující složku ──
        if (!string.IsNullOrWhiteSpace(listing.DriveFolderId))
        {
            var existingUrl = $"https://drive.google.com/drive/folders/{listing.DriveFolderId}";
            var existingName = ListingExportContentBuilder.SanitizeName($"{listing.Title} [{listing.LocationText}]");
            var existingTotal = listing.Photos.Count;
            logger.LogInformation("Drive export: vracím existující složku {Id}", listing.DriveFolderId);
            return new DriveExportResultDto(existingUrl, existingName, listing.DriveFolderId, listing.DriveInspectionFolderId,
                PhotosTotal: existingTotal);
        }

        var driveService = await CreateDriveServiceAsync();
        var rootFolderId = configuration["GoogleDriveExport:RootFolderId"]
            ?? throw new InvalidOperationException("GoogleDriveExport:RootFolderId není nakonfigurováno");

        var folderName = ListingExportContentBuilder.SanitizeName($"{listing.Title} [{listing.LocationText}]");
        var folder = await CreateFolderAsync(driveService, folderName, rootFolderId, ct);
        await SetPublicReadAsync(driveService, folder.Id, ct);

        logger.LogInformation("Vytvořena Drive složka: {Name} ({Id})", folderName, folder.Id);

        var photos = listing.Photos.OrderBy(p => p.Order).Take(20).ToList();
        var photoLinks = new List<PhotoLink>();
        if (photos.Count > 0)
        {
            var fotoFolder = await CreateFolderAsync(driveService, "Fotky_z_inzeratu", folder.Id, ct);
            await SetPublicReadAsync(driveService, fotoFolder.Id, ct);
            photoLinks = await UploadPhotosWithLinksAsync(driveService, photos, fotoFolder.Id, ct);
            if (photoLinks.Count < photos.Count)
                logger.LogWarning("Drive export: nahráno pouze {Uploaded}/{Total} fotek – {Skipped} fotek se nepodařilo stáhnout",
                    photoLinks.Count, photos.Count, photos.Count - photoLinks.Count);
            else
                logger.LogInformation("Nahráno {Count}/{Total} fotek do Drive", photoLinks.Count, photos.Count);
        }

        var infoId = await UploadTextAsync(driveService, "INFO.md",
            ListingExportContentBuilder.BuildInfoMarkdown(listing, photoLinks), "text/markdown", folder.Id, ct);
        await SetPublicReadAsync(driveService, infoId, ct);

        var dataId = await UploadTextAsync(driveService, "DATA.json",
            ListingExportContentBuilder.BuildDataJson(listing), "application/json", folder.Id, ct);
        await SetPublicReadAsync(driveService, dataId, ct);

        if (photoLinks.Count > 0)
        {
            var linksId = await UploadTextAsync(driveService, "FOTKY_LINKY.md",
                ListingExportContentBuilder.BuildPhotoLinksMarkdown(listing, photoLinks), "text/markdown", folder.Id, ct);
            await SetPublicReadAsync(driveService, linksId, ct);
        }

        var folderUrl = $"https://drive.google.com/drive/folders/{folder.Id}";
        var aiId = await UploadTextAsync(driveService, "AI_INSTRUKCE.md",
            ListingExportContentBuilder.BuildAiInstructions(listing, photoLinks, folderUrl), "text/markdown", folder.Id, ct);
        await SetPublicReadAsync(driveService, aiId, ct);

        var myfotoFolder = await CreateFolderAsync(driveService, "Moje_fotky_z_prohlidky", folder.Id, ct);
        await SetPublicReadAsync(driveService, myfotoFolder.Id, ct);

        // ── Uložíme folder IDs do DB ──────────────────────────────────────────
        listing.DriveFolderId = folder.Id;
        listing.DriveInspectionFolderId = myfotoFolder.Id;
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Export dokončen a folder IDs uloženy do DB: {Url} [{Uploaded}/{Total} fotek]",
            folderUrl, photoLinks.Count, photos.Count);
        return new DriveExportResultDto(folderUrl, folderName, folder.Id, myfotoFolder.Id,
            PhotosUploaded: photoLinks.Count, PhotosTotal: photos.Count);
    }

    // ── Google Drive helpers ────────────────────────────────────────────────

    public async Task<DriveScanResultDto> ScanDriveInspectionFolderAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await dbContext.Listings
            .AsNoTracking()
            .Select(l => new { l.Id, l.DriveInspectionFolderId })
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        if (string.IsNullOrWhiteSpace(listing.DriveInspectionFolderId))
            throw new InvalidOperationException(
                "Inzerát nemá DriveInspectionFolderId – nejprve proveďte export na Google Drive.");

        var driveService = await CreateDriveServiceAsync();

        // ── Načti seznam souborů z GD složky ─────────────────────────────
        var listReq = driveService.Files.List();
        listReq.Q = $"'{listing.DriveInspectionFolderId}' in parents " +
                    $"and mimeType != 'application/vnd.google-apps.folder' " +
                    $"and trashed = false";
        listReq.Fields = "files(id,name,size,createdTime,mimeType)";
        listReq.PageSize = 1000;
        var listResult = await listReq.ExecuteAsync(ct);
        var driveFiles = listResult.Files ?? [];

        if (driveFiles.Count == 0)
            return new DriveScanResultDto(0, 0, 0, "Složka na Google Drive je prázdná.");

        // ── Existující záznamy v DB – deduplikace dle OriginalFileName ────
        var existingNames = await dbContext.UserListingPhotos
            .Where(p => p.ListingId == listingId)
            .Select(p => p.OriginalFileName)
            .ToHashSetAsync(ct);

        var photosBaseUrl = configuration["PHOTOS_PUBLIC_BASE_URL"]
            ?? Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
            ?? "http://localhost:5001";
        var inspDir = Path.Combine(env.WebRootPath, "uploads", "listings", listingId.ToString(), "inspection");
        Directory.CreateDirectory(inspDir);

        // Zjisti aktuální max index pro číslování souborů (nepřepisuj existující)
        var existingCount = await dbContext.UserListingPhotos
            .CountAsync(p => p.ListingId == listingId, ct);

        int imported = 0, skipped = 0, fileIndex = existingCount;
        var now = DateTime.UtcNow;

        foreach (var gdf in driveFiles)
        {
            ct.ThrowIfCancellationRequested();

            // Přeskoč soubory, které už máme (dle jména)
            if (existingNames.Contains(gdf.Name))
            {
                skipped++;
                continue;
            }

            // Stáhni soubor z GD
            try
            {
                using var ms = new MemoryStream();
                var dlReq = driveService.Files.Get(gdf.Id);
                dlReq.MediaDownloader.ProgressChanged += progress =>
                {
                    if (progress.Status == Google.Apis.Download.DownloadStatus.Failed)
                        logger.LogWarning("GD download failed for {Name}: {Ex}", gdf.Name, progress.Exception?.Message);
                };
                await dlReq.DownloadAsync(ms, ct);
                var data = ms.ToArray();
                if (data.Length == 0)
                {
                    logger.LogWarning("GD soubor {Name} je prázdný, přeskahuji", gdf.Name);
                    skipped++;
                    continue;
                }

                var rawExt = Path.GetExtension(gdf.Name).ToLowerInvariant();
                var ext = rawExt is ".jpg" or ".jpeg" or ".png" or ".heic" or ".heif" or ".webp"
                    ? rawExt : ".jpg";
                var safeBase = Path.GetFileNameWithoutExtension(gdf.Name)
                    .Replace(" ", "_")
                    .Replace("/", "_")
                    .Replace("\\", "_");
                var localFileName = $"{fileIndex:D3}_{safeBase}{ext}";
                var fullPath = Path.Combine(inspDir, localFileName);
                await System.IO.File.WriteAllBytesAsync(fullPath, data, ct);

                var storedUrl = $"{photosBaseUrl.TrimEnd('/')}/uploads/listings/{listingId}/inspection/{localFileName}";

                dbContext.UserListingPhotos.Add(new UserListingPhoto
                {
                    Id = Guid.NewGuid(),
                    ListingId = listingId,
                    StoredUrl = storedUrl,
                    OriginalFileName = gdf.Name,
                    FileSizeBytes = data.Length,
                    TakenAt = gdf.CreatedTimeDateTimeOffset?.UtcDateTime ?? now,
                    UploadedAt = now
                });

                existingNames.Add(gdf.Name);
                fileIndex++;
                imported++;

                logger.LogInformation("GD scan import: {Name} → {LocalFile}", gdf.Name, localFileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "GD scan: nelze stáhnout soubor {Id} ({Name})", gdf.Id, gdf.Name);
                skipped++;
            }
        }

        if (imported > 0)
            await dbContext.SaveChangesAsync(ct);

        var msg = $"Importováno {imported} nových fotek z Google Drive, přeskočeno {skipped} (již existují nebo chyba).";
        logger.LogInformation("GD inspection scan pro {ListingId}: {Msg}", listingId, msg);
        return new DriveScanResultDto(imported, skipped, driveFiles.Count, msg);
    }

    public async Task<List<DriveFileDto>> ListAnalysisFilesAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await dbContext.Listings
            .AsNoTracking()
            .Select(l => new { l.Id, l.DriveFolderId })
            .FirstOrDefaultAsync(l => l.Id == listingId, ct);

        if (listing is null || string.IsNullOrWhiteSpace(listing.DriveFolderId))
            return [];

        try
        {
            var driveService = await CreateDriveServiceAsync();
            var req = driveService.Files.List();
            req.Q = $"'{listing.DriveFolderId}' in parents " +
                    $"and mimeType != 'application/vnd.google-apps.folder' " +
                    $"and trashed = false";
            req.Fields = "files(id,name,webViewLink,modifiedTime)";
            req.PageSize = 200;
            req.OrderBy = "modifiedTime desc";
            var result = await req.ExecuteAsync(ct);
            return (result.Files ?? [])
                .Where(f => f.Name.Contains("analyz", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("analýz", StringComparison.OrdinalIgnoreCase))
                .Select(f => new DriveFileDto(
                    f.Id,
                    f.Name,
                    f.WebViewLink ?? $"https://drive.google.com/file/d/{f.Id}/view",
                    f.ModifiedTimeDateTimeOffset?.UtcDateTime))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nelze načíst analýzy z Drive pro listing {Id}", listingId);
            return [];
        }
    }

    private async Task<DriveService> CreateDriveServiceAsync()
    {
        // Preferujeme OAuth UserToken (soubory vlastní uživatel, má storage quota)
        var tokenPath = configuration["GoogleDriveExport:UserTokenPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "secrets", "google-drive-token.json");

        if (System.IO.File.Exists(tokenPath))
        {
            logger.LogInformation("Drive auth: OAuth UserCredential z {Path}", tokenPath);
            return await CreateDriveServiceFromTokenAsync(tokenPath);
        }

        // Fallback: Service Account
        logger.LogWarning("Drive auth: OAuth token nenalezen ({Path}), používám Service Account – upload souborů může selhat kvůli kvótě", tokenPath);
        var credPath = configuration["GoogleDriveExport:ServiceAccountCredentialsPath"]
            ?? throw new InvalidOperationException("GoogleDriveExport:ServiceAccountCredentialsPath není nakonfigurováno");

        var credJson = await System.IO.File.ReadAllTextAsync(credPath);
        var credential = GoogleCredential
            .FromJson(credJson)
            .CreateScoped(DriveService.Scope.Drive);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "RealEstateAggregator"
        });
    }

    /// <summary>
    /// Vytvoří DriveService z uloženého OAuth tokenu.
    /// Soubor musí mít formát: { "client_id": "...", "client_secret": "...", "refresh_token": "..." }
    /// Získáš ho přes Google OAuth Playground: https://developers.google.com/oauthplayground/
    /// </summary>
    private static async Task<DriveService> CreateDriveServiceFromTokenAsync(string tokenPath)
    {
        var raw = await System.IO.File.ReadAllTextAsync(tokenPath);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var clientId = root.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje client_id");
        var clientSecret = root.GetProperty("client_secret").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje client_secret");
        var refreshToken = root.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje refresh_token");

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [DriveService.Scope.Drive]
        });

        var tokenResponse = new TokenResponse { RefreshToken = refreshToken };
        var userCredential = new UserCredential(flow, "user", tokenResponse);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = userCredential,
            ApplicationName = "RealEstateAggregator"
        });
    }

    private static async Task<DriveFile> CreateFolderAsync(DriveService drive, string name, string parentId, CancellationToken ct)
    {
        var folder = new DriveFile
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = [parentId]
        };
        var req = drive.Files.Create(folder);
        req.Fields = "id,name";
        return await req.ExecuteAsync(ct);
    }

    private static async Task<string> UploadTextAsync(DriveService drive, string fileName, string content, string mimeType, string parentId, CancellationToken ct)
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var req = drive.Files.Create(meta, stream, mimeType);
        req.Fields = "id";
        var result = await req.UploadAsync(ct);
        if (result.Status == Google.Apis.Upload.UploadStatus.Failed)
            throw new InvalidOperationException($"Upload souboru '{fileName}' selhal: {result.Exception?.Message}", result.Exception);
        return req.ResponseBody!.Id;
    }

    private static async Task<string> UploadBytesAsync(DriveService drive, string fileName, byte[] data, string parentId, CancellationToken ct, string contentType = "image/jpeg")
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(data);
        var req = drive.Files.Create(meta, stream, contentType);
        req.Fields = "id";
        var result = await req.UploadAsync(ct);
        if (result.Status == Google.Apis.Upload.UploadStatus.Failed)
            throw new InvalidOperationException($"Upload fotky '{fileName}' selhal: {result.Exception?.Message}", result.Exception);
        return req.ResponseBody!.Id;
    }

    public async Task UploadInspectionPhotosAsync(
        string inspectionFolderId,
        IReadOnlyList<(string Name, byte[] Data, string ContentType)> files,
        CancellationToken ct = default)
    {
        var driveService = await CreateDriveServiceAsync();
        foreach (var (name, data, contentType) in files)
        {
            var fileId = await UploadBytesAsync(driveService, name, data, inspectionFolderId, ct, contentType);
            await SetPublicReadAsync(driveService, fileId, ct);
            logger.LogInformation("Nahrána fotka z prohlídky na Drive: {Name}", name);
        }
    }

    private async Task SetPublicReadAsync(DriveService drive, string fileId, CancellationToken ct)
    {
        // Retry se exponenciálním backoffem – Drive API občas vrátí 429 nebo 500
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var perm = new Permission { Type = "anyone", Role = "reader", AllowFileDiscovery = false };
                await drive.Permissions.Create(perm, fileId).ExecuteAsync(ct);
                return; // úspěch
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning("SetPublicRead pokus {Attempt}/{Max} selhal pro {Id}: {Msg}", attempt, maxAttempts, fileId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
    }

    /// <summary>Nahraje fotky a vrátí seznam PhotoLink pro každou nahranou fotku.</summary>
    private async Task<List<PhotoLink>> UploadPhotosWithLinksAsync(
        DriveService drive, List<ListingPhoto> photos, string folderId, CancellationToken ct)
    {
        var result = new List<PhotoLink>();
        var http = httpClientFactory.CreateClient("DrivePhotoDownload");
        http.Timeout = TimeSpan.FromSeconds(30);

        for (int i = 0; i < photos.Count; i++)
        {
            var photo = photos[i];

            try
            {
                byte[] bytes;

                // 1. Preferujeme lokální stored_url – přežije expiraci CDN
                var localBytes = await TryReadStoredPhotoFromDiskAsync(photo.StoredUrl, ct);
                if (localBytes is not null)
                {
                    bytes = localBytes;
                    logger.LogDebug("Fotka #{Idx}: načtena z lokálního úložiště", i + 1);
                }
                else
                {
                    // 2. Fallback: stáhneme z original_url (CDN)
                    var url = photo.OriginalUrl;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        logger.LogWarning("Přeskočena fotka #{Idx}: nemá ani stored_url ani original_url", i + 1);
                        continue;
                    }

                    // Retry 3× s exponenciálním backoffem pro nestabilní CDN
                    bytes = null!;
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        try { bytes = await http.GetByteArrayAsync(url, ct); break; }
                        catch (Exception ex) when (attempt < 3)
                        {
                            logger.LogWarning("Stahování fotky {Url} pokus {A}/3: {Msg}", url, attempt, ex.Message);
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                        }
                    }
                }

                var urlForExt = photo.StoredUrl ?? photo.OriginalUrl ?? "";
                var rawExt = Path.GetExtension(urlForExt.Split('?')[0]).ToLowerInvariant();
                var ext = rawExt is ".jpg" or ".jpeg" or ".png" or ".webp" ? rawExt : ".jpg";
                var name = $"foto_{i + 1:D2}{ext}";
                var fileId = await UploadBytesAsync(drive, name, bytes, folderId, ct);
                await SetPublicReadAsync(drive, fileId, ct);
                var viewUrl = $"https://drive.google.com/file/d/{fileId}/view";
                result.Add(new PhotoLink(name, viewUrl));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Přeskočena fotka #{Idx}", i + 1);
            }
        }
        return result;
    }

    /// <summary>
    /// Pokusí se načíst fotku přímo z lokálního disku místo HTTP downloadem.
    /// stored_url formát: {base}/uploads/listings/{id}/photos/{file}
    /// → soubor: {WebRootPath}/uploads/listings/{id}/photos/{file}
    /// Vrátí null pokud stored_url není nastavena nebo soubor neexistuje.
    /// </summary>
    private async Task<byte[]?> TryReadStoredPhotoFromDiskAsync(string? storedUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storedUrl) || string.IsNullOrWhiteSpace(env.WebRootPath))
            return null;

        const string uploadsMarker = "/uploads/";
        var idx = storedUrl.IndexOf(uploadsMarker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // "/uploads/listings/{id}/photos/{file}" → www root relative path
        var relativePart = storedUrl[(idx + 1)..]; // "uploads/listings/..."
        var fullPath = Path.Combine(env.WebRootPath, relativePart.Replace('/', Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(fullPath)) return null;

        return await System.IO.File.ReadAllBytesAsync(fullPath, ct);
    }
}
