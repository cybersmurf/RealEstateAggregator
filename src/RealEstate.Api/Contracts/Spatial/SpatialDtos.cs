namespace RealEstate.Api.Contracts.Spatial;

/// <summary>Bod na mapě pro zobrazení v Leaflet.</summary>
public record ListingMapPointDto(
    Guid Id,
    string Title,
    decimal? Price,
    string LocationText,
    double Latitude,
    double Longitude,
    string PropertyType,
    string OfferType,
    string? MainPhotoUrl,
    string SourceCode
);

/// <summary>Request pro prostorové vyhledávání uvnitř polygonu/koridoru.</summary>
public record SpatialSearchRequestDto(
    /// <summary>WKT polygon v EPSG:4326, nebo null pro bounding-box search.</summary>
    string? PolygonWkt,
    double? BboxMinLat,
    double? BboxMinLon,
    double? BboxMaxLat,
    double? BboxMaxLon,
    string? PropertyType,
    string? OfferType,
    decimal? PriceMin,
    decimal? PriceMax,
    int Page = 1,
    int PageSize = 200
);

/// <summary>Request pro vytvoření koridoru (buffer kolem trasy mezi dvěma body/městy).</summary>
public record CorridorRequestDto(
    /// <summary>Počáteční město nebo GPS souřadnice "lat,lon"</summary>
    string Start,
    /// <summary>Cílové město nebo GPS souřadnice "lat,lon"</summary>
    string End,
    /// <summary>Šířka koridoru v metrech na každou stranu od trasy (default 5000 = 5km)</summary>
    int BufferMeters = 5000,
    /// <summary>Použít reálnou trasu přes OSRM (true) nebo přímou linii (false)</summary>
    bool UseRoute = true,
    /// <summary>Volitelně uložit oblast do databáze s tímto názvem</summary>
    string? SaveAsName = null
);

/// <summary>Výsledek operace s koridorem.</summary>
public record CorridorResultDto(
    string PolygonWkt,
    double StartLat,
    double StartLon,
    double EndLat,
    double EndLon,
    int BufferMeters,
    int ListingCount,
    Guid? SavedAreaId
);

/// <summary>Uložená prostorová oblast.</summary>
public record SpatialAreaDto(
    Guid Id,
    string Name,
    string? Description,
    string AreaType,
    string? StartCity,
    string? EndCity,
    int? BufferMeters,
    bool IsActive,
    DateTime CreatedAt
);
