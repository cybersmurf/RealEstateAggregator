using RealEstate.Api.Services;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// Poloha domu vůči sousedům: samostatný / přisazený z jedné strany / řadový / rohový – určuje se z venkovních fotek.
public class HousePositionTests
{
    private static ListingPhoto Photo(int order, string? category, string? description = null, string? labels = null)
        => new() { Id = Guid.NewGuid(), Order = order, PhotoCategory = category, PhotoDescription = description, PhotoLabels = labels };

    [Fact]
    public void SelectPhotos_AerialFirst_ThenExterior_ThenLand()
    {
        var photos = new[]
        {
            Photo(0, "living_room", "Obývací pokoj s krbovými kamny."),
            Photo(1, "land", "Zahrada s ovocnými stromy."),
            Photo(2, "exterior", "Pohled na rodinný dům z ulice."),
            Photo(3, "exterior", "Letecký pohled na rodinný dům s uzavřeným dvorem."),
            Photo(4, "land", "Snímek z dronu: pozemek a okolní zástavba."),
        };

        var selected = HousePositionService.SelectPhotos(photos, 6);

        Assert.Equal([3, 4, 2, 1], selected.Select(p => p.Order));
    }

    [Fact]
    public void SelectPhotos_SkipsVisualizations()
    {
        var photos = new[]
        {
            Photo(0, "exterior", "Vizualizace: Pohled na dvůr s upravenou zahradou.", """["visualization","garden"]"""),
            Photo(1, "exterior", "Pohled na vnitřní dvůr rodinného domu."),
        };

        Assert.Equal([1], HousePositionService.SelectPhotos(photos, 6).Select(p => p.Order));
    }

    [Fact]
    public void SelectPhotos_RespectsLimit_AndIgnoresUnclassified()
    {
        var photos = Enumerable.Range(0, 10).Select(i => Photo(i, "exterior")).Append(Photo(10, null)).ToList();

        Assert.Equal(HousePositionService.MaxPhotos, HousePositionService.SelectPhotos(photos, HousePositionService.MaxPhotos).Count);
    }

    [Fact]
    public void SelectPhotos_NoExterior_IsEmpty()
        => Assert.Empty(HousePositionService.SelectPhotos([Photo(0, "kitchen"), Photo(1, "floor_plan")], 6));

    [Theory]
    [InlineData("detached", "detached")]
    [InlineData("Semi-Detached", "semi_detached")]
    [InlineData("semi detached", "semi_detached")]
    [InlineData("TERRACED", "terraced")]
    [InlineData("corner", "corner")]
    [InlineData("unknown", "unknown")]
    public void ParseVerdict_NormalizesPosition(string raw, string expected)
    {
        var verdict = HousePositionService.ParseVerdict($$"""{"position":"{{raw}}","reason":"Vlevo vjezd, vpravo sousedova stodola."}""");

        Assert.NotNull(verdict);
        Assert.Equal(expected, verdict.Position);
        Assert.Equal("Vlevo vjezd, vpravo sousedova stodola.", verdict.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("není JSON")]
    [InlineData("""{"position":"row_house","reason":"neznámá hodnota"}""")]
    [InlineData("""{"position":3}""")]
    [InlineData("""["detached"]""")]
    public void ParseVerdict_UnusableAnswer_IsNull(string? raw)
        => Assert.Null(HousePositionService.ParseVerdict(raw));

    [Fact]
    public void WithEvidenceNote_NoAerialPhoto_SaysSoInReason()
    {
        Assert.Equal("Vlevo vjezd.", HousePositionService.WithEvidenceNote("Vlevo vjezd.", hasAerial: true));
        Assert.Equal($"{HousePositionService.NoAerialNote} Vlevo vjezd.", HousePositionService.WithEvidenceNote("Vlevo vjezd.", hasAerial: false));
        Assert.Equal(HousePositionService.NoAerialNote, HousePositionService.WithEvidenceNote("", hasAerial: false));
    }

    [Theory]
    [InlineData("Letecký pohled na rodinný dům s uzavřeným dvorem.", true)]
    [InlineData("Snímek z dronu: pozemek a okolní zástavba.", true)]
    [InlineData("Pohled na rodinný dům z ulice.", false)]
    [InlineData(null, false)]
    public void IsAerial_FollowsClassifierDescription(string? description, bool expected)
        => Assert.Equal(expected, HousePositionService.IsAerial(Photo(0, "exterior", description)));

    [Theory]
    [InlineData("detached", "samostatný")]
    [InlineData("semi_detached", "přisazený z jedné strany")]
    [InlineData("terraced", "řadový")]
    [InlineData("corner", "rohový")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Label_IsCzech_AndNullWhenUndetermined(string? position, string? expected)
        => Assert.Equal(expected, HousePositions.Label(position));
}
