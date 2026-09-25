namespace RealEstate.Api.Contracts.Market;

/// <summary>
/// Srovnání ceny za m² inzerátu s mediánem srovnatelných inzerátů v lokalitě.
/// <paramref name="Scope"/>: "municipality+disposition" | "municipality" | "district+disposition" | "district" | "region" | "none".
/// <paramref name="Verdict"/>: "pod mediánem" | "kolem mediánu" | "nad mediánem" (pásmo ±10 %), null při nedostatku dat.
/// </summary>
public sealed record MarketComparisonDto(
    Guid ListingId,
    decimal? ListingPricePerM2,
    decimal? MedianPricePerM2,
    decimal? P25PricePerM2,
    decimal? P75PricePerM2,
    int SampleSize,
    double? DeviationPct,
    string Scope,
    string ScopeLabel,
    string? Verdict);

/// <summary>
/// Hrubý výnos z pronájmu: nájem/m²/měsíc × 12 ÷ prodejní cena/m² × 100.
/// U prodeje se počítá z vlastní ceny inzerátu a mediánu nájmů v lokalitě, u pronájmu naopak.
/// </summary>
public sealed record RentalYieldDto(
    Guid ListingId,
    decimal? SalePricePerM2Median,
    decimal? MonthlyRentPerM2Median,
    double? GrossYieldPct,
    int SaleSampleSize,
    int RentSampleSize,
    string ScopeLabel,
    string? Note);

/// <summary>Řádek tržního reportu za lokalitu (+ typ nemovitosti, u bytů i dispozici).</summary>
public sealed record MarketReportRowDto(
    string Locality,
    string? PropertyType,
    string? Disposition,
    int ActiveCount,
    int SoldLast12M,
    decimal? MedianPricePerM2Sale,
    decimal? MedianRentPerM2,
    double? GrossYieldPct,
    double? MedianDaysToDeactivate,
    double? MedianDaysOnMarketActive);

public sealed record MarketReportDto(
    DateTime GeneratedAt,
    IReadOnlyList<MarketReportRowDto> Rows,
    int TotalActive,
    int TotalDeactivatedLast12M);
