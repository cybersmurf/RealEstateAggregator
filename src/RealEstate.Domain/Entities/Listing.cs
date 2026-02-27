using Pgvector;
using RealEstate.Domain.Enums;

namespace RealEstate.Domain.Entities;

public class Listing
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string SourceCode { get; set; } = null!;
    public string SourceName { get; set; } = null!;
    public string? ExternalId { get; set; }
    public string Url { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    
    public PropertyType PropertyType { get; set; }
    public OfferType OfferType { get; set; }
    
    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }
    
    public string LocationText { get; set; } = null!;
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }
    
    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
    public int? Rooms { get; set; }
    public string? Disposition { get; set; }     // "1+1", "1+kk", "4+1", atd.
    public bool? HasKitchen { get; set; }
    
    public string? ConstructionType { get; set; }
    public string? Condition { get; set; }
    
    public DateTime? CreatedAtSource { get; set; }
    public DateTime? UpdatedAtSource { get; set; }
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
    
    // � GPS souřadnice (z scraperu nebo Nominatim geocoderu)
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime? GeocodedAt { get; set; }
    public string? GeocodeSource { get; set; }   // "scraper" | "nominatim" | "manual"

    // �🔥 pgvector: OpenAI embedding (1536 dimensions) for semantic search
    public Vector? DescriptionEmbedding { get; set; }

    // Export folder IDs – ukládámy po úspěšném exportu, aby byl export idempotentní
    // a upload fotek z prohlídky fungoval i po refreshi stránky
    public string? DriveFolderId { get; set; }
    public string? DriveInspectionFolderId { get; set; }
    public string? OneDriveFolderId { get; set; }
    public string? OneDriveInspectionFolderId { get; set; }
    
    // ── Ollama text features ────────────────────────────────────────────────────
    /// <summary>JSON pole 5 klíčových tagů z popisu: ["sklep","zahrada","novostavba", ...]</summary>
    public string? SmartTags { get; set; }
    public DateTime? SmartTagsAt { get; set; }

    /// <summary>Strukturovaná data z popisu (jsonb): rok_stavby, patro, vytah, sklep, zahrada, ...</summary>
    public string? AiNormalizedData { get; set; }
    public DateTime? AiNormalizedAt { get; set; }

    /// <summary>Cenový signál: "low" | "fair" | "high"</summary>
    public string? PriceSignal { get; set; }
    public string? PriceSignalReason { get; set; }
    public DateTime? PriceSignalAt { get; set; }

    // Navigation properties
    public Source Source { get; set; } = null!;
    public ICollection<ListingPhoto> Photos { get; set; } = new List<ListingPhoto>();
    public ICollection<UserListingState> UserStates { get; set; } = new List<UserListingState>();
    public ICollection<AnalysisJob> AnalysisJobs { get; set; } = new List<AnalysisJob>();
    public ICollection<ListingAnalysis> Analyses { get; set; } = new List<ListingAnalysis>();
}
