using RealEstate.Api.Contracts.Export;

namespace RealEstate.Api.Services;

public interface IGoogleDriveExportService
{
    Task<DriveExportResultDto> ExportListingToDriveAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>Nahrá fotky z prohlídky do podsložky Moje_fotky_z_prohlidky na Google Drive.</summary>
    Task UploadInspectionPhotosAsync(
        string inspectionFolderId,
        IReadOnlyList<(string Name, byte[] Data, string ContentType)> files,
        CancellationToken ct = default);
}
