namespace RealEstate.Domain.Entities;

public class ScrapeRun
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string SourceCode { get; set; } = null!;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string Status { get; set; } = "Running"; // "Running", "Succeeded", "Failed"

    public int TotalSeen { get; set; }
    public int TotalNew { get; set; }
    public int TotalUpdated { get; set; }
    public int TotalInactivated { get; set; }
    public string? ErrorMessage { get; set; }

    // Navigation properties
    public Source Source { get; set; } = null!;
}
