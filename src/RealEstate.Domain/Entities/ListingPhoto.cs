namespace RealEstate.Domain.Entities;

public class ListingPhoto
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public string OriginalUrl { get; set; } = null!;
    public string? StoredUrl { get; set; }
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? AiDescription { get; set; }

    // ── Výsledky Ollama Vision klasifikace ──────────────────────────────────
    /// <summary>Kategorie fotky: exterior|interior|kitchen|bathroom|living_room|bedroom|attic|basement|garage|land|floor_plan|damage|other</summary>
    public string? PhotoCategory { get; set; }
    /// <summary>Popis fotky vygenerovaný Ollama Vision česky (1-2 věty o stavu/materiálech/detailech)</summary>
    public string? PhotoDescription { get; set; }
    /// <summary>JSON pole tagů: ["mold","water_damage","renovation_needed",...]</summary>
    public string? PhotoLabels { get; set; }
    /// <summary>True pokud Ollama Vision detekovala viditelné poškození</summary>
    public bool DamageDetected { get; set; }
    /// <summary>Confidence skóre klasifikace 0.00–1.00</summary>
    public decimal? ClassificationConfidence { get; set; }
    /// <summary>Čas kdy byla fotka klasifikována</summary>
    public DateTime? ClassifiedAt { get; set; }

    // Navigation property
    public Listing Listing { get; set; } = null!;
}
