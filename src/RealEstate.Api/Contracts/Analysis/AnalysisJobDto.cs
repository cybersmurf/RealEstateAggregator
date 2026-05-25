namespace RealEstate.Api.Contracts.Analysis;

public sealed class AnalysisJobDto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Running, Succeeded, Failed
    public string StorageProvider { get; set; } = string.Empty;
    public string? StorageUrl { get; set; } // link na Google Drive
    public DateTime RequestedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
