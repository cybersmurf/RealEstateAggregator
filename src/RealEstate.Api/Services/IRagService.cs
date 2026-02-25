using RealEstate.Api.Contracts.Rag;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services;

public interface IRagService
{
    /// <summary>Uloží analýzu do DB a vygeneruje embedding (pokud OpenAI nakonfigurováno).</summary>
    Task<ListingAnalysisDto> SaveAnalysisAsync(Guid listingId, string content, string source, string? title, CancellationToken ct);

    /// <summary>RAG dotaz nad analýzami konkrétního inzerátu.</summary>
    Task<AskResponseDto> AskListingAsync(Guid listingId, string question, int topK, CancellationToken ct);

    /// <summary>RAG dotaz přes všechny inzeráty (cross-listing).</summary>
    Task<AskResponseDto> AskGeneralAsync(string question, int topK, CancellationToken ct);

    /// <summary>Embeduje popis inzerátu jako analýzu source="auto". Idempotentní – přeskočí pokud už existuje.</summary>
    Task<(ListingAnalysisDto? Analysis, bool AlreadyExists)> EmbedListingDescriptionAsync(Guid listingId, CancellationToken ct);

    /// <summary>Bulk embed pro všechny inzeráty bez "auto" analýzy. Vrátí počet zpracovaných.</summary>
    Task<int> BulkEmbedDescriptionsAsync(int limit, CancellationToken ct);

    /// <summary>Vrátí všechny analýzy pro daný inzerát.</summary>
    Task<List<ListingAnalysisDto>> GetAnalysesAsync(Guid listingId, CancellationToken ct);

    /// <summary>Smaže analýzu podle ID.</summary>
    Task<bool> DeleteAnalysisAsync(Guid analysisId, CancellationToken ct);
}
