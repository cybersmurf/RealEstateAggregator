using System.Text.Json;

namespace RealEstate.Api.Contracts.Rag;

public record ListingAnalysisDto(
    Guid Id,
    Guid ListingId,
    string Content,
    string? Title,
    string Source,
    bool HasEmbedding,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    /// <summary>Strukturované hodnocení (JSON objekt), pokud ho analýza má.</summary>
    JsonElement? Structured = null
);
