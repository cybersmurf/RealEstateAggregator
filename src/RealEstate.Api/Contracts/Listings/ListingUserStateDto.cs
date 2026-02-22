namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingUserStateDto
{
    public string Status { get; set; } = "New"; // "New", "Liked", "Disliked", "ToVisit", "Visited"
    public string? Notes { get; set; }
    public DateTime? LastUpdated { get; set; }
}
