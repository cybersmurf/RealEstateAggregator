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
    public string? Disposition { get; set; }                 // "1+1", "2+kk", atd.
    public int? Rooms { get; set; }
    public string? Condition { get; set; }                   // "Novostavba", "Po rekonstrukci", ...
    public string? ConstructionType { get; set; }            // "Cihla", "Panel", "Dřevo", ...
    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }
    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime? UpdatedAtSource { get; set; }
    public bool IsActive { get; set; }

    // Thumbnail pro kartový pohled
    public string? ThumbnailUrl { get; set; }

    // User-specific info (denormalizované kvůli UI)
    public string UserStatus { get; set; } = "New"; // "New", "Liked", "Disliked"...
    public bool HasNotes { get; set; }
    public DateTime? UserStatusLastUpdated { get; set; }

    /// <summary>Strukturovaná AI data z popisu (jsonb as string) – pro badge výpočet v UI.</summary>
    public string? AiNormalizedData { get; set; }

    /// <summary>Price signal: "low" | "fair" | "high" – výstup AI cenové analýzy.</summary>
    public string? PriceSignal { get; set; }

    /// <summary>Smart tagy jako JSON pole stringů, např. ["Cihla","Zahrada","Garáž"].</summary>
    public string? SmartTags { get; set; }
}
