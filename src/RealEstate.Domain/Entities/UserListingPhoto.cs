namespace RealEstate.Domain.Entities;

/// <summary>
/// User's own photos uploaded from property visits.
/// Stored in cloud storage (Google Drive/OneDrive) or locally.
/// </summary>
public class UserListingPhoto
{
    /// <summary>
    /// Primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Reference to listing
    /// </summary>
    public Guid ListingId { get; set; }

    /// <summary>
    /// Storage provider URL/ID
    /// - Google Drive: file ID (e.g., "1a2b3c4d5e")
    /// - OneDrive: item ID
    /// - Local: relative path (e.g., "uploads/listings/xxx/photo.jpg")
    /// </summary>
    public string StoredUrl { get; set; } = null!;

    /// <summary>
    /// Original filename uploaded by user
    /// </summary>
    public string OriginalFileName { get; set; } = null!;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Photo taken timestamp (from EXIF if available, otherwise uploaded_at)
    /// </summary>
    public DateTime TakenAt { get; set; }

    /// <summary>
    /// Upload timestamp
    /// </summary>
    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// User notes about this photo (optional)
    /// </summary>
    public string? Notes { get; set; }

    // Navigation property
    public Listing Listing { get; set; } = null!;
}
