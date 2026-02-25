using RealEstate.Api.Contracts.Export;

namespace RealEstate.Api.Services;

public interface IOneDriveExportService
{
    Task<DriveExportResultDto> ExportListingAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>Uloží text (analýzu) jako soubor do existující OneDrive složky.</summary>
    Task SaveAnalysisAsync(string folderId, string content, CancellationToken ct = default);

    /// <summary>Nahrá fotky z prohlídky do podsložky Moje_fotky_z_prohlidky na OneDrive.</summary>
    Task UploadInspectionPhotosAsync(
        string inspectionFolderId,
        IReadOnlyList<(string Name, byte[] Data, string ContentType)> files,
        CancellationToken ct = default);
}
