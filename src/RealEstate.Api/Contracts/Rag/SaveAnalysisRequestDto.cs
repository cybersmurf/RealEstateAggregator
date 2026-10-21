using System.Text.Json;

namespace RealEstate.Api.Contracts.Rag;

public record SaveAnalysisRequestDto(
    string Content,
    string? Title = null,
    string Source = "manual",
    /// <summary>Volitelné strukturované hodnocení (JSON objekt) – uloží se vedle textu do listing_analyses.structured.</summary>
    JsonElement? Structured = null
);
