using RealEstate.Api.Services.Market;
using RealEstate.Domain.Enums;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  MarketStatsService – čisté pomocné funkce pro srovnání Kč/m² s lokalitou,
//  výnos z pronájmu a tržní report. Datová část (EF) se netestuje.
// ─────────────────────────────────────────────────────────────────
public class MarketStatsTests
{
    // ── LocalityKey ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Brno", "Brno - Židenice, Táborská", "Brno-Židenice")]
    [InlineData("Brno", "Brno-Královo Pole, Purkyňova", "Brno-Královo Pole")]
    [InlineData("Brno", "Brno – střed", "Brno-střed")]
    [InlineData(null, "Brno - Bystrc, Rakovecká 12", "Brno-Bystrc")]
    public void LocalityKey_Brno_ExtractsCityPart(string? municipality, string locationText, string expected)
    {
        Assert.Equal(expected, MarketStatsService.LocalityKey(municipality, locationText));
    }

    [Fact]
    public void LocalityKey_PlainMunicipality_IsReturnedAsIs()
    {
        Assert.Equal("Znojmo", MarketStatsService.LocalityKey("Znojmo", "Znojmo, Pražská 12, okres Znojmo"));
    }

    [Fact]
    public void LocalityKey_MunicipalityWins_OverBrnoInLocationText()
    {
        Assert.Equal("Znojmo", MarketStatsService.LocalityKey("Znojmo", "Znojmo, směr Brno - Vídeň"));
    }

    [Fact]
    public void LocalityKey_MissingMunicipality_FallsBackToFirstSegment()
    {
        Assert.Equal("Moravský Krumlov", MarketStatsService.LocalityKey(null, "Moravský Krumlov, okres Znojmo"));
        Assert.Equal("Hrušovany nad Jevišovkou", MarketStatsService.LocalityKey("", "  Hrušovany nad Jevišovkou "));
    }

    [Fact]
    public void LocalityKey_NothingKnown_ReturnsPlaceholder()
    {
        Assert.Equal("Neznámá lokalita", MarketStatsService.LocalityKey(null, null));
        Assert.Equal("Neznámá lokalita", MarketStatsService.LocalityKey(" ", ", okres Znojmo"));
    }

    // ── Percentily / medián ──────────────────────────────────────────

    [Fact]
    public void Median_OddCount_ReturnsMiddleValue()
    {
        Assert.Equal(50_000m, MarketStatsService.Median(new decimal[] { 70_000, 30_000, 50_000 }));
    }

    [Fact]
    public void Median_EvenCount_InterpolatesBetweenMiddleValues()
    {
        Assert.Equal(45_000m, MarketStatsService.Median(new decimal[] { 30_000, 40_000, 50_000, 60_000 }));
    }

    [Fact]
    public void Percentile_QuartilesWithLinearInterpolation()
    {
        var values = new decimal[] { 10, 20, 30, 40, 50 };
        Assert.Equal(20m, MarketStatsService.Percentile(values, 0.25));
        Assert.Equal(40m, MarketStatsService.Percentile(values, 0.75));
        Assert.Equal(10m, MarketStatsService.Percentile(values, 0));
        Assert.Equal(50m, MarketStatsService.Percentile(values, 1));
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsThatValue()
    {
        Assert.Equal(42m, MarketStatsService.Percentile(new decimal[] { 42 }, 0.5));
        Assert.Equal(7.5, MarketStatsService.Percentile(new[] { 7.5 }, 0.25));
    }

    [Fact]
    public void Percentile_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => MarketStatsService.Percentile(Array.Empty<decimal>(), 0.5));
    }

    [Fact]
    public void Percentile_Double_MedianOfDays()
    {
        Assert.Equal(21.0, MarketStatsService.Percentile(new[] { 3.0, 21.0, 90.0 }, 0.5));
    }

    // ── Verdikt ±10 % ────────────────────────────────────────────────

    [Theory]
    [InlineData(-25.0, "pod mediánem")]
    [InlineData(-10.1, "pod mediánem")]
    [InlineData(-10.0, "kolem mediánu")]
    [InlineData(0.0, "kolem mediánu")]
    [InlineData(10.0, "kolem mediánu")]
    [InlineData(10.1, "nad mediánem")]
    [InlineData(40.0, "nad mediánem")]
    public void Verdict_UsesTenPercentBand(double deviationPct, string expected)
    {
        Assert.Equal(expected, MarketStatsService.Verdict(deviationPct));
    }

    [Fact]
    public void DeviationPct_IsRelativeToMedian()
    {
        Assert.Equal(20.0, MarketStatsService.DeviationPct(60_000m, 50_000m), 6);
        Assert.Equal(-20.0, MarketStatsService.DeviationPct(40_000m, 50_000m), 6);
        Assert.Equal(0.0, MarketStatsService.DeviationPct(40_000m, 0m));
    }

    // ── Hrubý výnos ──────────────────────────────────────────────────

    [Fact]
    public void GrossYield_RentTimesTwelveOverPrice()
    {
        // 300 Kč/m²/měs. × 12 = 3 600 Kč/m²/rok ÷ 80 000 Kč/m² = 4,5 %
        Assert.Equal(4.5, MarketStatsService.GrossYieldPct(80_000m, 300m), 6);
    }

    [Fact]
    public void GrossYield_ZeroPrice_ReturnsZero()
    {
        Assert.Equal(0.0, MarketStatsService.GrossYieldPct(0m, 300m));
    }

    // ── Cena za m² a věrohodnost ─────────────────────────────────────

    [Fact]
    public void PricePerM2_Apartment_UsesBuiltUpArea()
    {
        Assert.Equal(80_000m, MarketStatsService.PricePerM2(PropertyType.Apartment, 4_800_000m, 60, 1_000));
    }

    [Fact]
    public void PricePerM2_Land_UsesLandArea()
    {
        Assert.Equal(1_500m, MarketStatsService.PricePerM2(PropertyType.Land, 1_500_000m, null, 1_000));
    }

    [Theory]
    [InlineData(7_300_000, 10.0)]   // kuchyň místo domu → 730 000 Kč/m²
    [InlineData(200_000, 100.0)]    // 2 000 Kč/m² – pod mezí
    [InlineData(5_000_000, null)]   // bez plochy
    [InlineData(null, 100.0)]       // bez ceny
    public void PricePerM2_Implausible_IsRejected(int? price, double? area)
    {
        Assert.Null(MarketStatsService.PricePerM2(PropertyType.House, price, area, null));
    }

    [Theory]
    [InlineData(18_000, 60, 300)]
    [InlineData(1_000, 60, null)]      // 16,7 Kč/m² – pod mezí
    [InlineData(90_000, 60, null)]     // 1 500 Kč/m² – nad mezí
    public void RentPerM2_AppliesPlausibilityBand(int rent, double area, int? expected)
    {
        var actual = MarketStatsService.RentPerM2(rent, area);
        if (expected is null)
            Assert.Null(actual);
        else
            Assert.Equal(expected.Value, actual!.Value, 0);
    }

    // ── Drobnosti ────────────────────────────────────────────────────

    [Theory]
    [InlineData("2+kk", "2+kk")]
    [InlineData("2 + KK", "2+kk")]
    [InlineData("  3+1 ", "3+1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeDisposition_CollapsesWhitespaceAndCase(string? input, string? expected)
    {
        Assert.Equal(expected, MarketStatsService.NormalizeDisposition(input));
    }

    [Theory]
    [InlineData(1, "1 inzerát")]
    [InlineData(3, "3 inzeráty")]
    [InlineData(5, "5 inzerátů")]
    [InlineData(12, "12 inzerátů")]
    public void CountLabel_CzechPlurals(int count, string expected)
    {
        Assert.Equal(expected, MarketStatsService.CountLabel(count));
    }
}
