namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingDetailDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public string LocationText { get; set; } = string.Empty;
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }

    public string PropertyType { get; set; } = string.Empty;
    public string OfferType { get; set; } = string.Empty;

    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }

    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
    public int? Rooms { get; set; }
    public string? Disposition { get; set; }   // "1+1", "2+kk", atd.
    public bool? HasKitchen { get; set; }
    public string? ConstructionType { get; set; }
    public string? Condition { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? CreatedAtSource { get; set; }
    public DateTime? UpdatedAtSource { get; set; }
    public bool IsActive { get; set; }

    public IReadOnlyList<ListingPhotoDto> Photos { get; set; } = Array.Empty<ListingPhotoDto>();

    public ListingUserStateDto UserState { get; set; } = new();

    // Google Drive / OneDrive export info
    public string? DriveFolderUrl { get; set; }
    public string? DriveInspectionFolderUrl { get; set; }
    public bool HasOneDriveExport { get; set; }
}
