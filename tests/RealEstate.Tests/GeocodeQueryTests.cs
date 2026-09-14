using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  ExtractCityFromLocationText – co se pošle Nominatimu.
//  Dřív první část adresy: "137" (č.p.) nebo "Dyjská" (ulice) padlo 100+ km vedle.
// ─────────────────────────────────────────────────────────────────
public class GeocodeQueryTests
{
    [Theory]
    [InlineData("Štítary", "Štítary")]
    [InlineData("Pohořelice, Jihomoravský kraj", "Pohořelice")]
    [InlineData("137, Borotice, okres Znojmo", "Borotice")]            // CENTURY21
    [InlineData("Chaloupky, Rozdrojovice, okres Znojmo", "Rozdrojovice")]
    [InlineData("Dyjská, Znojmo", "Znojmo")]                            // PREMIAREALITY
    [InlineData("243/7, Znojmo", "Znojmo")]
    [InlineData("Hlavní 137, Šanov, okres Znojmo", "Šanov")]
    [InlineData("671 61 Znojmo", "Znojmo")]                              // BAZOS
    [InlineData("Oslnovice, okr. Znojmo", "Oslnovice")]
    public void PicksMunicipality(string locationText, string expected)
    {
        Assert.Equal(expected, SpatialService.ExtractCityFromLocationText(locationText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mikulov 15 min., Brno 42 min., Znojmo 38 min.")]  // šablonový text HVREALITY
    public void NothingUsable_ReturnsEmpty(string locationText)
    {
        Assert.Equal("", SpatialService.ExtractCityFromLocationText(locationText));
    }
}
