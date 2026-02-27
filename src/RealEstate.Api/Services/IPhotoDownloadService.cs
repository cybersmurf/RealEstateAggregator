namespace RealEstate.Api.Services;

/// <summary>
/// Downloads listing photos from external URLs and stores them locally.
/// </summary>
public interface IPhotoDownloadService
{
    /// <summary>Downloads a batch of photos that have no stored_url yet. Optionally filtered to a single listing
    /// or only to listings with user status Liked / ToVisit / Visited.</summary>
    Task<PhotoDownloadResultDto> DownloadBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null, bool onlyMyListings = false);

    /// <summary>Returns stats about photo download progress.</summary>
    Task<PhotoDownloadStatsDto> GetStatsAsync(CancellationToken ct);
}

public record PhotoDownloadResultDto(
    int Processed,
    int Succeeded,
    int Failed,
    int RemainingWithoutStored,
    double AvgMsPerPhoto);

public record PhotoDownloadStatsDto(
    int Total,
    int WithStoredUrl,
    int WithoutStoredUrl,
    double PercentStored);
