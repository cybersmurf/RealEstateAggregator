namespace RealEstate.Infrastructure.Storage;

/// <summary>
/// Interface for cloud/local storage operations.
/// Implementations: LocalStorageService, GoogleDriveStorageService, OneDriveStorageService
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Upload file to configured storage provider.
    /// </summary>
    /// <param name="stream">File content stream</param>
    /// <param name="fileName">Original filename (e.g., "IMG_1234.jpg")</param>
    /// <param name="folder">Logical folder path (e.g., "listings/{listingId}/my_photos")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Stored URL (Drive file ID or local path)</returns>
    Task<string> UploadFileAsync(
        Stream stream,
        string fileName,
        string folder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get public/shareable URL for file.
    /// </summary>
    /// <param name="storedUrl">URL returned from UploadFileAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Public URL (or null if not available)</returns>
    Task<string?> GetFileUrlAsync(
        string storedUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete file from storage.
    /// </summary>
    /// <param name="storedUrl">URL returned from UploadFileAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteFileAsync(
        string storedUrl,
        CancellationToken cancellationToken = default);
}
