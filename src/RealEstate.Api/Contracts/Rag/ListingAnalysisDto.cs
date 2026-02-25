namespace RealEstate.Api.Contracts.Rag;

public record ListingAnalysisDto(
    Guid Id,
    Guid ListingId,
    string Content,
    string? Title,
    string Source,
    bool HasEmbedding,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
