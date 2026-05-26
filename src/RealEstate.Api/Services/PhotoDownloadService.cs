using Microsoft.EntityFrameworkCore;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Storage;

namespace RealEstate.Api.Services;

/// <summary>
/// Downloads listing photos from their original external URLs
/// and stores them locally via IStorageService.
/// <br/>
/// <b>stored_url</b> format: relative path like
/// <c>/uploads/listings/{id}/photos/0_abc.jpg</c>
/// (Blazor app prepends ApiPublicUrl at render time)
/// </summary>
public sealed class PhotoDownloadService(
    RealEstateDbContext db,
    IStorageService storageService,
    IHttpClientFactory httpClientFactory,
    ILogger<PhotoDownloadService> logger) : IPhotoDownloadService
{

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

        // Pokud je zadán konkrétní listing, stáhni VŠECHNY jeho fotky (žádný batching).
        // BatchSize se aplikuje jen pro globální bulk bez listingId.
        var orderedQuery = query.OrderBy(p => p.ListingId).ThenBy(p => p.Order);
        var photos = await (listingId.HasValue ? orderedQuery : orderedQuery.Take(batchSize))
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            var remaining0 = await query.CountAsync(ct);
            return new PhotoDownloadResultDto(0, 0, 0, remaining0, 0);
        }

        int succeeded = 0, failed = 0, purged = 0;
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

                    // Permanentní chyba (404 Not Found, 410 Gone) → foto záznam je mrtvý, smaž ho.
                    // Dočasné chyby (5xx, 429, 403) → ponech záznam, zkusíme příště.
                    if (response.StatusCode is System.Net.HttpStatusCode.NotFound
                                             or System.Net.HttpStatusCode.Gone)
                    {
                        db.ListingPhotos.Remove(photo);
                        logger.LogInformation(
                            "Deleted dead photo record {Order} for listing {ListingId} (HTTP {Status})",
                            photo.Order, photo.ListingId, (int)response.StatusCode);
                        purged++;
                    }

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

                // ── Store relative path only – Blazor prepends ApiPublicUrl at render time ─
                photo.StoredUrl = $"/{relativePath.TrimStart('/')}";

                logger.LogDebug(
                    "Stored photo {Order} for listing {ListingId} → {Url}",
                    photo.Order, photo.ListingId, photo.StoredUrl);

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
        if (succeeded > 0 || purged > 0)
            await db.SaveChangesAsync(ct);

        stopwatch.Stop();
        var avgMs = photos.Count > 0 ? stopwatch.ElapsedMilliseconds / (double)photos.Count : 0;

        var remaining = await query.CountAsync(ct);

        logger.LogInformation(
            "Photo batch: {Processed} processed, {Succeeded} succeeded, {Failed} failed, {Purged} purged. Remaining: {Remaining}",
            photos.Count, succeeded, failed, purged, remaining);

        return new PhotoDownloadResultDto(photos.Count, succeeded, failed, remaining, Math.Round(avgMs, 0), purged);
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
