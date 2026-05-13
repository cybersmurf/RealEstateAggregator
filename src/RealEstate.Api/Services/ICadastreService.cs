using RealEstate.Api.Contracts.Cadastre;

namespace RealEstate.Api.Services;

public interface ICadastreService
{
    /// <summary>Vrátí katastrální data pro inzerát (pokud existují).</summary>
    Task<ListingCadastreDto?> GetAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>
    /// Spustí RUIAN vyhledávání pro jeden inzerát a výsledek uloží do DB.
    /// Pokud záznam již existuje, přepíše ho.
    /// </summary>
    Task<ListingCadastreDto> FetchAndSaveAsync(Guid listingId, CancellationToken ct = default);

    /// <summary>
    /// Manuálně uloží / aktualizuje katastrální data (LV, břemena, výměra).
    /// Zachová RUIAN kód pokud byl nalezen dříve.
    /// </summary>
    Task<ListingCadastreDto> SaveManualDataAsync(Guid listingId, SaveCadastreDataRequest request, CancellationToken ct = default);

    /// <summary>
    /// Hromadné RUIAN vyhledávání pro inzeráty bez katastrálních dat.
    /// Vrací statistiky.
    /// </summary>
    Task<BulkRuianResultDto> BulkFetchAsync(int batchSize = 50, CancellationToken ct = default);

    /// <summary>
    /// OCR screenshot ze stránky nahlíženídokn.cuzk.cz přes Ollama Vision.
    /// Extrahuje parcelní číslo, LV, výměru, druh pozemku, vlastníky, břemena.
    /// Uloží/přepíše katastrální data s FetchStatus = "ocr".
    /// </summary>
    Task<CadastreOcrResultDto> OcrScreenshotAsync(Guid listingId, byte[] imageData, CancellationToken ct = default);

    /// <summary>
    /// OCR PDF dokumentu ze státní správy přes Mistral OCR API (mistral-ocr-latest).
    /// Přijímá byte pole PDF, extrahuje text a pak strukturuje KN data přes Mistral chat.
    /// Uloží/přepíše katastrální data s FetchStatus = "mistral-ocr".
    /// Vyžaduje MISTRAL_API_KEY v environment nebo konfiguraci.
    /// </summary>
    Task<CadastreOcrResultDto> OcrPdfAsync(Guid listingId, byte[] pdfData, CancellationToken ct = default);
}
