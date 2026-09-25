namespace RealEstate.Api.Contracts.Market;

/// <summary>Veřejná lokalita pro SEO stránky /lokalita/{slug} a sitemap.</summary>
public sealed record LocalityIndexItemDto(string Name, string Slug, string? District, int ActiveCount);

/// <summary>Veřejný agregát obce – žádné přebírané texty, jen statistika z vlastních dat.</summary>
public sealed record LocalityPublicStatsDto(
    string Name,
    string Slug,
    string? District,
    int ActiveCount,
    int ActiveHouses,
    int ActiveLand,
    int ActiveApartments,
    int ActiveRent,
    int NewLast30Days,
    int DeactivatedLast12Months,
    decimal? MedianHousePrice,
    decimal? MedianHousePricePerM2,
    decimal? MedianLandPricePerM2,
    decimal? MedianApartmentPricePerM2,
    decimal? MedianApartmentRentPerM2,
    double? MedianDaysOnMarketActive,
    double? MedianDaysToDeactivate,
    int PriceDropsLast90Days,
    DateTime GeneratedAt);
