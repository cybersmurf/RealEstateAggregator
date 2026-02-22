using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class UserListingState
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ListingId { get; set; }
    public string Status { get; set; } = "New"; // "New", "Liked", "Disliked", "ToVisit", "Visited"
    public string? Notes { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    
    // Navigation property
    public Listing Listing { get; set; } = null!;
}
