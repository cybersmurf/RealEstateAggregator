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
}
