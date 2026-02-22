namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingSummaryDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty; // "REMAX", "MMR", "PRODEJMETO"...
    public string Title { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }
    public string PropertyType { get; set; } = string.Empty; // "House", "Cottage", ...
    public string OfferType { get; set; } = string.Empty;    // "Sale", "Rent"
    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }
    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime? UpdatedAtSource { get; set; }
    public bool IsActive { get; set; }

    // User-specific info (denormalizované kvůli UI)
    public string UserStatus { get; set; } = "New"; // "New", "Liked", "Disliked"...
    public bool HasNotes { get; set; }
    public DateTime? UserStatusLastUpdated { get; set; }
}
