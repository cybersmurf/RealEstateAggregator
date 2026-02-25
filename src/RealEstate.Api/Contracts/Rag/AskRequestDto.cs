namespace RealEstate.Api.Contracts.Rag;

public record AskRequestDto(
    string Question,
    int TopK = 5
);
