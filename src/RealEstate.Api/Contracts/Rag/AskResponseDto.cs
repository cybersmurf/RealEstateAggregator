namespace RealEstate.Api.Contracts.Rag;

public record AskResponseDto(
    string Answer,
    List<AnalysisChunkDto> Sources,
    bool HasEmbeddings
);

public record AnalysisChunkDto(
    Guid AnalysisId,
    string? Title,
    string ContentExcerpt,
    string Source,
    double Similarity,
    DateTime CreatedAt
);
