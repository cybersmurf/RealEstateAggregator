using RealEstate.Api.Contracts.Spatial;

namespace RealEstate.Api.Services;

public interface ISpatialService
{
    /// <summary>
    /// Geokóduje adresu (město) pomocí Nominatim → (lat, lon).
    /// </summary>
    Task<(double Lat, double Lon)?> GeocodeAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Vytvoří WKT polygon koridoru (buffer) kolem trasy mezi dvěma body.
    /// Používá OSRM pro reálnou trasu nebo přímou linii.
    /// Buffer se provádí v metrické projekci EPSG:5514 (S-JTSK) pro přesné výsledky v ČR.
    /// </summary>
    Task<CorridorResultDto> BuildCorridorAsync(CorridorRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Vyhledá inzeráty s GPS souřadnicemi uvnitř zadaného WKT polygonu nebo bounding boxu.
    /// </summary>
    Task<IReadOnlyList<ListingMapPointDto>> SearchInAreaAsync(SpatialSearchRequestDto request, CancellationToken ct = default);

    /// <summary>
    /// Vrátí všechny aktivní inzeráty s GPS souřadnicemi (pro mapu).
    /// Max 2000 výsledků pro výkon.
    /// </summary>
    Task<IReadOnlyList<ListingMapPointDto>> GetAllMapPointsAsync(
        string? propertyType = null,
        string? offerType = null,
        decimal? priceMax = null,
        CancellationToken ct = default);

    /// <summary>Uloží pojmenovanou prostorovou oblast do databáze.</summary>
    Task<SpatialAreaDto> SaveAreaAsync(string name, string polygonWkt, string areaType,
        string? startCity, string? endCity, int? bufferMeters, CancellationToken ct = default);

    /// <summary>Vrátí seznam uložených prostorových oblastí.</summary>
    Task<IReadOnlyList<SpatialAreaDto>> GetSavedAreasAsync(CancellationToken ct = default);

    /// <summary>Statistika geokódování – kolik inzerátů má/nemá GPS souřadnice.</summary>
    Task<object> GetGeocodeStatsAsync(CancellationToken ct = default);
}
