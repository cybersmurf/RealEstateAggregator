namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingPhotoDto
{
    public Guid Id { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string? StoredUrl { get; set; }
    public int Order { get; set; }
}
