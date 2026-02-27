using Microsoft.EntityFrameworkCore;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Services;

/// <summary>
/// Downloads listing photos from their original external URLs
/// and stores them locally via IStorageService.
/// <br/>
/// <b>stored_url</b> format: full public URL like
/// <c>http://localhost:5001/uploads/listings/{id}/photos/0_abc.jpg</c>
/// </summary>
public sealed class PhotoDownloadService(
    RealEstateDbContext db,
    IStorageService storageService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<PhotoDownloadService> logger) : IPhotoDownloadService
{
    // Set PHOTOS_PUBLIC_BASE_URL env var in production to the externally accessible API URL
    private readonly string _publicBaseUrl =
        Environment.GetEnvironmentVariable("PHOTOS_PUBLIC_BASE_URL")
        ?? configuration["Photos:PublicBaseUrl"]
        ?? "http://localhost:5001";

    // Statusy, které spadají pod "moje inzeráty" (= uživatel je aktivně sleduje)
    private static readonly string[] MyListingStatuses = ["Liked", "ToVisit", "Visited"];

    public async Task<PhotoDownloadResultDto> DownloadBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null, bool onlyMyListings = false)
    {
        batchSize = Math.Clamp(batchSize, 1, 200);

        // Základní query: jen fotky bez stored_url
        var query = db.ListingPhotos
            .Where(p => p.StoredUrl == null);

        // Filtr na konkrétní inzerát
        if (listingId.HasValue)
            query = query.Where(p => p.ListingId == listingId.Value);

        // Filtr onlyMyListings: jen inzeráty kde má uživatel stav Liked / ToVisit / Visited
        if (onlyMyListings)
            query = query.Where(p =>
                db.UserListingStates.Any(s =>
                    s.ListingId == p.ListingId &&
                    MyListingStatuses.Contains(s.Status)));

        var photos = await query
            .OrderBy(p => p.ListingId)
            .ThenBy(p => p.Order)
            .Take(batchSize)
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remaining0 = await query.CountAsync(ct);
            return new PhotoDownloadResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var httpClient = httpClientFactory.CreateClient("PhotoDownload");

        foreach (var photo in photos)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // ── Download photo stream ───────────────────────────────────
                // URL může obsahovat mezery (prodejme.to filename) – Safe parse přes Uri
                var requestUrl = Uri.TryCreate(photo.OriginalUrl, UriKind.Absolute, out var parsedUri)
                    ? parsedUri.AbsoluteUri
                    : photo.OriginalUrl;
                using var response = await httpClient.GetAsync(requestUrl, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Photo download HTTP {Status} for listing {ListingId} photo {Order}: {Url}",
                        (int)response.StatusCode, photo.ListingId, photo.Order, photo.OriginalUrl);
                    failed++;
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                var ext = contentType switch
                {
                    "image/png"  => ".png",
                    "image/webp" => ".webp",
                    "image/gif"  => ".gif",
                    _            => ".jpg",
                };

                // ── Store via IStorageService ───────────────────────────────
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var folder = $"listings/{photo.ListingId}/photos";
                var fileName = $"{photo.Order}{ext}";

                var relativePath = await storageService.UploadFileAsync(stream, fileName, folder, ct);

                // ── Build public URL and update DB ─────────────────────────────────────────
                var publicUrl = $"{_publicBaseUrl.TrimEnd('/')}/{relativePath}";
                photo.StoredUrl = publicUrl;

                logger.LogDebug(
                    "Stored photo {Order} for listing {ListingId} → {Url}",
                    photo.Order, photo.ListingId, publicUrl);

                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to download/store photo {Order} for listing {ListingId}: {Url}",
                    photo.Order, photo.ListingId, photo.OriginalUrl);
                failed++;
            }
        }

        // ── Save all updates in one shot ────────────────────────────────────
        if (succeeded > 0)
            await db.SaveChangesAsync(ct);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;

        var remaining = await db.ListingPhotos
            .CountAsync(p => p.StoredUrl == null && (listingId == null || p.ListingId == listingId), ct);

        logger.LogInformation(
            "Photo batch: {Processed} processed, {Succeeded} succeeded, {Failed} failed. Remaining: {Remaining}",
            photos.Count, succeeded, failed, remaining);

        return new PhotoDownloadResultDto(photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0));
    }

    public async Task<PhotoDownloadStatsDto> GetStatsAsync(CancellationToken ct)
    {
        var total         = await db.ListingPhotos.CountAsync(ct);
        var withStoredUrl = await db.ListingPhotos.CountAsync(p => p.StoredUrl != null, ct);
        var without       = total - withStoredUrl;
        var pct           = total > 0 ? Math.Round(withStoredUrl * 100.0 / total, 1) : 0;

        return new PhotoDownloadStatsDto(total, withStoredUrl, without, pct);
    }
}
