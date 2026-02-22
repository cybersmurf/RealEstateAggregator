using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class AnalysisJob
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public Guid UserId { get; set; }

    public AnalysisStatus Status { get; set; } = AnalysisStatus.Pending;
    public string? StorageProvider { get; set; }  // GoogleDrive, OneDrive, Local
    public string? StoragePath { get; set; }
    public string? StorageUrl { get; set; }
    
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    
    // Navigation property
    public Listing Listing { get; set; } = null!;
}
