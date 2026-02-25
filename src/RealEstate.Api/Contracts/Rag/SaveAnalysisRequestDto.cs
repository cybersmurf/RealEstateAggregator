namespace RealEstate.Api.Contracts.Rag;

public record SaveAnalysisRequestDto(
    string Content,
    string? Title = null,
    string Source = "manual"
);
