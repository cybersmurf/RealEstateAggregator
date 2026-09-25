using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Services;

/// <summary>
/// Hromadné mazání lokálních kopií fotek inzerátů. Pracuje výhradně nad <c>listing_photos</c>
/// (soubory pod <c>listings/{id}/photos/</c>) – fotky z prohlídky jsou uživatelovy a zůstávají.
/// </summary>
public sealed class PhotoPurgeService(
    RealEstateDbContext db,
    IStorageService storageService,
    IWebHostEnvironment env,
    IConfiguration configuration,
    ILogger<PhotoPurgeService> logger) : IPhotoPurgeService
{
    public async Task<PhotoPurgeResultDto> PurgeStoredAsync(bool onlyClassified, int olderThanDays, int batchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 5000);

        var query = BuildQuery(onlyClassified, olderThanDays);

        var photos = await query
            .OrderBy(p => p.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        int deleted = 0, failed = 0;

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();

            var storedUrl = photo.StoredUrl!;
            try
            {
                await storageService.DeleteFileAsync(storedUrl, ct);
                DeleteLocalFallback(storedUrl);
                photo.StoredUrl = null;
                deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Purge: failed to delete {Url} (listing {ListingId} order {Order})",
                    storedUrl, photo.ListingId, photo.Order);
                failed++;
            }
        }

        if (deleted > 0)
            await db.SaveChangesAsync(ct);

        var remaining = await BuildQuery(onlyClassified, olderThanDays).CountAsync(ct);

        logger.LogInformation("Purge stored photos: {Deleted} deleted, {Failed} failed, {Remaining} remaining.",
            deleted, failed, remaining);

        return new PhotoPurgeResultDto(photos.Count, deleted, failed, remaining);
    }

    private IQueryable<ListingPhoto> BuildQuery(bool onlyClassified, int olderThanDays)
    {
        var query = db.ListingPhotos.Where(p => p.StoredUrl != null);

        if (onlyClassified)
            query = query.Where(p => p.ClassifiedAt != null);

        if (olderThanDays > 0)
        {
            var threshold = DateTime.UtcNow.AddDays(-olderThanDays);
            query = query.Where(p => p.CreatedAt < threshold);
        }

        return query;
    }

    /// <summary>
    /// Starší záznamy mají stored_url s absolutní base URL, které LocalStorageService nerozpozná –
    /// dočistíme přímo z wwwroot. Cesta musí zůstat pod uploads/listings/, nikdy jinam.
    /// </summary>
    private void DeleteLocalFallback(string storedUrl)
    {
        try
        {
            var baseUrl = (Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
                           ?? configuration["Photos:PublicBaseUrl"]
                           ?? "http://localhost:5001").TrimEnd('/');

            var relative = storedUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
                ? storedUrl[(baseUrl.Length + 1)..]
                : storedUrl.TrimStart('/');

            if (!relative.StartsWith("uploads/listings/", StringComparison.OrdinalIgnoreCase))
                return;

            var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var fullPath = Path.Combine([env.WebRootPath, .. segments]);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Purge: local fallback delete failed for {Url}", storedUrl);
        }
    }
}
