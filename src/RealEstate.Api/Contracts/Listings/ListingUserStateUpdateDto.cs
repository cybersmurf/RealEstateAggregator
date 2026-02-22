namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingUserStateUpdateDto
{
    public string Status { get; set; } = "New";
    public string? Notes { get; set; }
}
