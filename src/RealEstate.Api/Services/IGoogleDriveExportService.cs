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

    /// <summary>
    /// Prohledá GD složku Moje_fotky_z_prohlidky, stáhne nové soubory
    /// a importuje je do user_listing_photos + uloží lokálně.
    /// Soubory, které jsou již v DB (dle OriginalFileName), se přeskočí.
    /// </summary>
    Task<DriveScanResultDto> ScanDriveInspectionFolderAsync(Guid listingId, CancellationToken ct = default);
}

public record DriveScanResultDto(
    int Imported,
    int Skipped,
    int TotalInFolder,
    string Message);
