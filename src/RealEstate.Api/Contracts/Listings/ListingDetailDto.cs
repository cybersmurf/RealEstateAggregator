namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingDetailDto
{
    public Guid Id { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    /// <summary>Původní text inzerátu – vyplněný jen pro správce; veřejně prázdný (autorská práva zdroje).</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Neutrální AI shrnutí popisu – veřejná náhrada původního textu.</summary>
    public string? Summary { get; set; }
    /// <summary>Zdroj má popis (i když ho DTO nevrací) – UI ví, že shrnutí teprve vznikne.</summary>
    public bool HasDescription { get; set; }

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
    /// <summary>Kdy inzerát zmizel ze zdroje (null u aktivních).</summary>
    public DateTime? DeactivatedAt { get; set; }
    /// <summary>Dny na trhu: aktivní od prvního spatření do teď, stažený do deaktivace.</summary>
    public int DaysOnMarket { get; set; }

    // Dražba
    public DateTime? AuctionDate { get; set; }
    public decimal? AuctionStartingPrice { get; set; }
    public decimal? AuctionDeposit { get; set; }

    public IReadOnlyList<ListingPhotoDto> Photos { get; set; } = Array.Empty<ListingPhotoDto>();

    public ListingUserStateDto UserState { get; set; } = new();

    // Google Drive export info
    public string? DriveFolderUrl { get; set; }
    public string? DriveInspectionFolderUrl { get; set; }

    /// <summary>Pokud je tento inzerát duplikátem (cross-source), ID primárního inzerátu.</summary>
    public Guid? DuplicateOfListingId { get; set; }
    /// <summary>Titulek primárního inzerátu (pokud je tohle duplikát).</summary>
    public string? DuplicateOfTitle { get; set; }
    /// <summary>Source code primárního inzerátu.</summary>
    public string? DuplicateOfSourceCode { get; set; }

    // Ollama text features
    /// <summary>JSON pole 5 klíčových tagů: ["sklep","zahrada","garáž"]</summary>
    public string? SmartTags { get; set; }
    /// <summary>Strukturovaná data z popisu (jsonb object jako string)</summary>
    public string? AiNormalizedData { get; set; }
    /// <summary>Cenový signál: "low" | "fair" | "high"</summary>
    public string? PriceSignal { get; set; }
    public string? PriceSignalReason { get; set; }
}
