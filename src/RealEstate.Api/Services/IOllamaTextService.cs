namespace RealEstate.Api.Services;

/// <summary>
/// Textové Ollama featury: smart tags, normalizace popisu, cenový signál, detekce duplikátů.
/// Používá llama3.2 text model (bez vision) – rychlejší a levnější než vision.
/// </summary>
public interface IOllamaTextService
{
    /// <summary>Dávkově generuje smart tagy (5 tagů) z popisu inzerátu.</summary>
    Task<OllamaTextBatchResultDto> BulkSmartTagsAsync(int batchSize, CancellationToken ct);

    /// <summary>Dávkově normalizuje popis – extrahuje rok stavby, patro, výtah, sklep, zahradu, ...</summary>
    Task<OllamaTextBatchResultDto> BulkNormalizeAsync(int batchSize, CancellationToken ct);

    /// <summary>Dávkově generuje cenový signál (low/fair/high) na základě lokality, plochy a stavu.</summary>
    Task<OllamaTextBatchResultDto> BulkPriceOpinionAsync(int batchSize, CancellationToken ct);

    /// <summary>Porovná dva inzeráty a vyhodnotí, zda jde o tutéž nemovitost.</summary>
    Task<DuplicateDetectionResultDto> DetectDuplicatesAsync(Guid listingId1, Guid listingId2, CancellationToken ct);

    /// <summary>Vrátí statistiku zpracovaných inzerátů pro každou funkci.</summary>
    Task<OllamaTextStatsDto> GetStatsAsync(CancellationToken ct);
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record OllamaTextBatchResultDto(
    int Processed,
    int Succeeded,
    int Failed,
    int RemainingUnprocessed,
    double AvgMsPerItem);

public record DuplicateDetectionResultDto(
    Guid ListingId1,
    Guid ListingId2,
    bool IsDuplicate,
    double ConfidenceScore,
    string Reasoning);

public record OllamaTextStatsDto(
    int TotalListings,
    int WithSmartTags,
    int WithNormalizedData,
    int WithPriceSignal,
    int PriceSignalLow,
    int PriceSignalFair,
    int PriceSignalHigh);
