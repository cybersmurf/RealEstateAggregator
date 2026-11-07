using RealEstate.Api.Services;

namespace RealEstate.Tests;

// Přibližná poloha (bod obce) se nesmí brát jako poloha domu – Tasovice 7. 11. 2026.
public class GpsPrecisionTests
{
    [Theory]
    [InlineData("nominatim", true)]
    [InlineData("sreality-area", true)]
    [InlineData("scraper", false)]
    [InlineData(null, false)]
    public void PriblizneZdrojePolohy(string? source, bool approximate)
    {
        Assert.Equal(approximate, GpsPrecision.IsApproximate(source));
    }
}
