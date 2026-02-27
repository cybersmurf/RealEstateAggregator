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

    /// <summary>
    /// AI-generated description of the photo content (llava vision model).
    /// Null = not yet analyzed.
    /// </summary>
    public string? AiDescription { get; set; }

    // ── AI Classification (Ollama Vision) ──────────────────────────────────
    /// <summary>
    /// Photo category: exterior, interior, kitchen, bathroom, living_room, bedroom,
    /// attic, basement, garage, land, floor_plan, damage, other
    /// </summary>
    public string? PhotoCategory { get; set; }

    /// <summary>JSON array of labels, e.g. ["mold","water_damage"]</summary>
    public string? PhotoLabels { get; set; }

    /// <summary>True if Ollama detected visible damage/defects.</summary>
    public bool DamageDetected { get; set; }

    /// <summary>Ollama confidence 0.0–1.0</summary>
    public decimal? ClassificationConfidence { get; set; }

    /// <summary>When this photo was classified. Null = not yet classified.</summary>
    public DateTime? ClassifiedAt { get; set; }

    // Navigation property
    public Listing Listing { get; set; } = null!;
}
