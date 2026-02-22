namespace RealEstate.Domain.Entities;

public class ListingPhoto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string OriginalUrl { get; set; } = null!;
    public string? StoredUrl { get; set; }
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation property
    public Listing Listing { get; set; } = null!;
}
