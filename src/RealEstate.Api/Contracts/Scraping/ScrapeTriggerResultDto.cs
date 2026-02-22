namespace RealEstate.Api.Contracts.Scraping;

public sealed class ScrapeTriggerResultDto
{
    public Guid JobId { get; set; }
    public string Status { get; set; } = "Queued"; // Queued, Started, Failed
    public string? Message { get; set; }
}
