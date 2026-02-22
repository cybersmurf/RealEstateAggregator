namespace RealEstate.Domain.Entities;

public class Source
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!; // "REMAX", "MMR", "PRODEJMETO"
    public string Name { get; set; } = null!;
    public string BaseUrl { get; set; } = null!;
    public bool IsActive { get; set; } = true;

    public bool SupportsUrlScrape { get; set; } = true;
    public bool SupportsListScrape { get; set; } = true;
    public string ScraperType { get; set; } = "Python"; // "Python", "PlaywrightNet"

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
    public ICollection<ScrapeRun> ScrapeRuns { get; set; } = new List<ScrapeRun>();
}

