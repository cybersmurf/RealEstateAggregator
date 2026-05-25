namespace RealEstate.Domain.Entities;

public sealed class ListingPriceHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }
    public decimal? Price { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>"scraper" | "manual" | "import"</summary>
    public string Source { get; set; } = "scraper";

    // Navigation
    public Listing Listing { get; set; } = null!;
}
