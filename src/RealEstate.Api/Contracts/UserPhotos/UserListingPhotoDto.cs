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
    string? AiDescription
);
