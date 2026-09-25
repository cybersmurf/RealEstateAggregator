using RealEstate.Api.Contracts.Market;

namespace RealEstate.Api.Services.Market;

/// <summary>Tržní statistiky: Kč/m² vs. lokalita, výnos z nájmu, doba na trhu.</summary>
public interface IMarketStatsService
{
    /// <summary>Srovnání ceny za m² s mediánem lokality. Null = inzerát neexistuje.</summary>
    Task<MarketComparisonDto?> GetComparisonAsync(Guid listingId, CancellationToken ct);

    /// <summary>Hrubý výnos z pronájmu pro byt/dům. Null = inzerát neexistuje.</summary>
    Task<RentalYieldDto?> GetRentalYieldAsync(Guid listingId, CancellationToken ct);

    /// <summary>Tržní report po lokalitách s volitelnými filtry.</summary>
    Task<MarketReportDto> GetReportAsync(string? municipality, string? district, string? propertyType, CancellationToken ct);
}
