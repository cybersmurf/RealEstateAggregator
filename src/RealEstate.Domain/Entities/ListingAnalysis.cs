using Pgvector;

namespace RealEstate.Domain.Entities;

/// <summary>
/// Uložená analýza inzerátu – text + pgvector embedding pro RAG dotazy.
/// Zdroj může být "manual" (uživatel), "claude", "perplexity", "mcp" atd.
/// </summary>
public class ListingAnalysis
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ListingId { get; set; }

    /// <summary>Celý text analýzy (markdown, plain text, …)</summary>
    public string Content { get; set; } = null!;

    /// <summary>OpenAI text-embedding-3-small (1536 dim)</summary>
    public Vector? Embedding { get; set; }

    /// <summary>Původ analýzy: "manual" | "claude" | "perplexity" | "mcp" | "ai"</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Volitelný nadpis (např. "Analýza ze dne 25.2.2026")</summary>
    public string? Title { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Listing Listing { get; set; } = null!;
}
