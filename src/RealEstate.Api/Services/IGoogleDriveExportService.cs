using RealEstate.Api.Contracts.Export;

namespace RealEstate.Api.Services;

public interface IGoogleDriveExportService
{
    Task<DriveExportResultDto> ExportListingToDriveAsync(Guid listingId, CancellationToken ct = default);
}
