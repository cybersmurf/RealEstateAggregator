namespace RealEstate.Api.Contracts.UserPhotos;

/// <summary>
/// DTO for user listing photo
/// </summary>
public record UserListingPhotoDto(
    Guid Id,
    string Url,
    string OriginalFileName,
    long FileSizeBytes,
    DateTime TakenAt,
    DateTime UploadedAt,
    string? Notes,
    string? AiDescription,
    /// <summary>Náhled ~480 px (fotky z telefonu mají 3–6 MB); null, když se ho nepodařilo vyrobit.</summary>
    string? ThumbnailUrl = null,
    /// <summary>Kategorie z klasifikace (kitchen, bathroom…), null = neklasifikováno.</summary>
    string? PhotoCategory = null
);
