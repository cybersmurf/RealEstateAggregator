namespace RealEstate.Api.Services;

public interface ILocalAnalysisService
{
    /// <summary>
    /// Spustí lokální analýzu inzerátu: vision popis fotek → text AI analýza → uloží do DB.
    /// </summary>
    /// <param name="chatModel">Override modelu pro text analýzu (null = výchozí z konfigurace). Příklad: "qwen3.5:9b"</param>
    Task<LocalAnalysisResultDto> AnalyzeAsync(Guid listingId, string? chatModel = null, CancellationToken ct = default);

    /// <summary>
    /// Vrátí DOCX jako byte[] z poslední uložené analýzy (nebo spustí novou pokud neexistuje).
    /// </summary>
    /// <param name="chatModel">Stejný model jako při analýze – vybere správnou analýzu z DB.</param>
    Task<byte[]?> ExportDocxAsync(Guid listingId, string? chatModel = null, CancellationToken ct = default);
}

public record LocalAnalysisResultDto(
    Guid AnalysisId,
    Guid ListingId,
    string Title,
    int PhotosDescribed,
    int PhotosTotal,
    double ElapsedSeconds,
    string MarkdownContent
);
