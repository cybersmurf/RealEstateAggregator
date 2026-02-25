namespace RealEstate.Api.Contracts.Export;

public record DriveExportResultDto(
    string FolderUrl,
    string FolderName,
    string FolderId,
    string? InspectionFolderId = null,
    int PhotosUploaded = 0,
    int PhotosTotal = 0
)
{
    /// <summary>true pokud všechny fotky byly nahrány bez chyby</summary>
    public bool AllPhotosUploaded => PhotosTotal == 0 || PhotosUploaded == PhotosTotal;
};
