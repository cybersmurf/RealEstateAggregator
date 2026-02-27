namespace RealEstate.Api.Services;

/// <summary>
/// Klasifikuje fotky nemovitostí pomocí lokálního Ollama Vision modelu (llama3.2-vision).
/// Pracuje jen s fotkami, které mají stored_url (staženy lokálně) a ještě nebyly klasifikovány.
/// </summary>
public interface IPhotoClassificationService
{
    /// <summary>Klasifikuje dávku fotek z inzerátu přes Ollama Vision API. Optionally filtered to a single listing.</summary>
    Task<PhotoClassificationResultDto> ClassifyBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null);

    /// <summary>Klasifikuje dávku fotek z prohlídky (user_listing_photos) přes Ollama Vision API. Optionally filtered to a single listing.</summary>
    Task<PhotoClassificationResultDto> ClassifyInspectionBatchAsync(int batchSize, CancellationToken ct, Guid? listingId = null);

    /// <summary>Vrátí statistiku klasifikovaných vs. neklasifikovaných fotek.</summary>
    Task<PhotoClassificationStatsDto> GetClassificationStatsAsync(CancellationToken ct);
}

public record PhotoClassificationResultDto(
    int Processed,
    int Succeeded,
    int Failed,
    int RemainingUnclassified,
    double AvgMsPerPhoto);

public record PhotoClassificationStatsDto(
    int Total,
    int Classified,
    int Unclassified,
    int WithDamage,
    double PercentClassified);
