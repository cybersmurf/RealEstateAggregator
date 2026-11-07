using RealEstate.Api.Services;
using RealEstate.Api.Services.Vision;

namespace RealEstate.Tests;

// Poloha domu z půdorysů v katastru (ČÚZK INSPIRE Buildings): společné zdi se měří, ne odhadují z obrázku.
// Fixtures = skutečné odpovědi WFS 7. 11. 2026 kolem GPS domů z kontroly 47 domů (okruh 40 m).
public class HouseAdjacencyTests
{
    private static List<CadastreBuilding> Fixture(string name, double lat, double lon)
    {
        var gml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "HousePosition", $"{name}.gml"));
        return CuzkBuildingService.ParseBuildings(gml, lat, lon)!;
    }

    private static CadastreBuilding Box(string name, double x1, double y1, double x2, double y2, string? use = "IndividualResidence")
        => new(Guid.NewGuid().ToString(), name, use, null, [],
            [[(x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1)]]);

    [Theory]
    // Suchohrdly 8126f8bd: řada č.p. 370–381, GPS na č.p. 380 se sousedy z obou stran (model řekl „detached")
    [InlineData("suchohrdly", 48.869946, 16.099273, "Suchohrdly", "terraced", "č.p. 380")]
    // Drnholec a485b9e0: č.p. 334 přiléhá k 335, na který navazuje 336 – krajní dům řady (makléř „Řadový")
    [InlineData("drnholec", 48.8639383398262, 16.4921431274455, "Drnholec", "corner", "č.p. 334")]
    // Oleksovice 990f8933: č.p. 234 a 233 tvoří dvojici (model řekl „detached", makléř „Rohový")
    [InlineData("oleksovice", 48.9043036843, 16.2387417653, "Oleksovice", "semi_detached", "č.p. 234")]
    // Štítary e4d4b140: č.p. 241 bez společné zdi (model řekl „semi", makléř „Samostatný")
    [InlineData("stitary2", 48.933559, 15.850037, "Štítary", "detached", "č.p. 241")]
    // Břežany c4390f34: GPS 2 m před č.p. 95 na chodníku – přiřadí se k němu; přiléhá k č.p. 93 (model „detached", makléř „Řadový")
    [InlineData("brezany", 48.8699226889555, 16.3417505468073, "Břežany", "semi_detached", "č.p. 95")]
    public void Analyze_RealCadastre_MeasuresSharedWalls(string fixture, double lat, double lon, string municipality, string expected, string building)
    {
        var verdict = HouseAdjacency.Analyze(Fixture(fixture, lat, lon), municipality);

        Assert.Equal(expected, verdict.Position);
        Assert.Equal(building, verdict.BuildingName);
        Assert.StartsWith("Katastr:", verdict.Reason);
    }

    [Fact]
    public void Analyze_PinOnPublicBuilding_IsInconclusive()
    {
        // Čejkovice 4cd4fd15: GPS u obecní budovy (PublicServices), nejbližší obytný dům 18 m – střed obce, ne dům
        var verdict = HouseAdjacency.Analyze(Fixture("cejkovice", 48.8777007259443, 16.2873259609881), "Čejkovice");

        Assert.Null(verdict.Position);
        Assert.False(verdict.IsConclusive);
        Assert.Contains("neleží na obytné budově", verdict.Reason);
    }

    [Fact]
    public void Analyze_BuildingInAnotherMunicipality_IsInconclusive()
    {
        var verdict = HouseAdjacency.Analyze(Fixture("oleksovice", 48.9043036843, 16.2387417653), "Hrušovany nad Jevišovkou");

        Assert.Null(verdict.Position);
        Assert.Contains("GPS míří jinam", verdict.Reason);
    }

    [Fact]
    public void Analyze_TerracedFixture_ListsBothNeighbours()
    {
        var verdict = HouseAdjacency.Analyze(Fixture("suchohrdly", 48.869946, 16.099273), "Suchohrdly");

        Assert.Equal(["č.p. 379", "č.p. 381"], verdict.Houses!.Select(h => h.Name).Order());
        Assert.All(verdict.Houses!, h => Assert.InRange(h.WallMeters, 8, 14));
    }

    [Fact]
    public void ParseBuildings_ReadsNumberUseAddressAndClosedFootprint()
    {
        var buildings = Fixture("stitary2", 48.933559, 15.850037);

        var house = Assert.Single(buildings, b => b.Name == "č.p. 241");
        Assert.Equal("IndividualResidence", house.CurrentUse);
        Assert.Contains("Štítary", house.AddressNames);
        Assert.StartsWith("CP.", house.ParcelId);
        Assert.True(house.HasHouseNumber);
        var ring = Assert.Single(house.Rings);
        Assert.Equal(ring[0], ring[^1]);
        Assert.True(HouseAdjacency.Contains(house, 0, 0));
    }

    [Fact]
    public void ParseBuildings_ExceptionReport_IsNull()
        => Assert.Null(CuzkBuildingService.ParseBuildings(
            """<ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1"><ows:Exception exceptionCode="InvalidParameterValue"/></ows:ExceptionReport>""", 49, 16));

    [Fact]
    public void WfsUrl_AsksForBuildingsInWgs84BoundingBox()
    {
        var url = CuzkBuildingService.WfsUrl(48.933559, 15.850037, 40);

        Assert.StartsWith(CuzkBuildingService.BuildingsWfs + "?", url);
        Assert.Contains("typeNames=bu:Building", url);
        Assert.Contains("srsName=urn:ogc:def:crs:EPSG::4326", url);
        Assert.Contains("bbox=48.933200,15.849490,48.933918,15.850584,urn:ogc:def:crs:EPSG::4326", url);
    }

    [Fact]
    public void Analyze_TwoHousesSharingWall_IsDuplex()
    {
        var verdict = HouseAdjacency.Analyze([Box("č.p. 1", -5, -5, 5, 5), Box("č.p. 2", 5, -5, 15, 5)], null);

        Assert.Equal(HousePositions.SemiDetached, verdict.Position);
        Assert.Equal(10, Assert.Single(verdict.Houses!).WallMeters, 1);
    }

    [Fact]
    public void Analyze_Row_MiddleIsTerraced_EndIsCorner()
    {
        CadastreBuilding[] row = [Box("č.p. 1", -15, -5, -5, 5), Box("č.p. 2", -5, -5, 5, 5), Box("č.p. 3", 5, -5, 15, 5)];
        Assert.Equal(HousePositions.Terraced, HouseAdjacency.Analyze(row, null).Position);

        CadastreBuilding[] end = [Box("č.p. 1", -5, -5, 5, 5), Box("č.p. 2", 5, -5, 15, 5), Box("č.p. 3", 15, -5, 25, 5)];
        Assert.Equal(HousePositions.Corner, HouseAdjacency.Analyze(end, null).Position);
    }

    [Fact]
    public void Analyze_OnlyOutbuildingAttached_IsDetachedWithNote()
    {
        var verdict = HouseAdjacency.Analyze([Box("č.p. 1", -5, -5, 5, 5), Box("bez čp/če", 5, -3, 11, 3, "Ancillary")], null);

        Assert.Equal(HousePositions.Detached, verdict.Position);
        Assert.Equal("bez čp/če", Assert.Single(verdict.Outbuildings!).Name);
        Assert.Contains("vedlejší stavba", verdict.Reason);
    }

    [Theory]
    [InlineData(6.0)]   // okapová ulička 1 m
    [InlineData(5.8)]   // 80 cm – pořád dva samostatné domy
    public void Analyze_ParallelWallsWithGap_IsDetached(double neighbourStart)
        => Assert.Equal(HousePositions.Detached,
            HouseAdjacency.Analyze([Box("č.p. 1", -5, -5, 5, 5), Box("č.p. 2", neighbourStart, -5, neighbourStart + 10, 5)], null).Position);

    [Fact]
    public void Analyze_TouchingOnlyAtCorner_IsDetached()
        => Assert.Equal(HousePositions.Detached,
            HouseAdjacency.Analyze([Box("č.p. 1", -5, -5, 5, 5), Box("č.p. 2", 5, 5, 15, 15)], null).Position);

    [Fact]
    public void Analyze_SameHouseNumberSplitIntoTwoFootprints_IsNotNeighbour()
        => Assert.Equal(HousePositions.Detached,
            HouseAdjacency.Analyze([Box("č.p. 1", -5, -5, 5, 5), Box("č.p. 1", 5, -5, 9, 5)], null).Position);

    [Fact]
    public void Analyze_NewBuildWithoutNumberButResidential_CountsAsHouse()
        => Assert.Equal(HousePositions.SemiDetached,
            HouseAdjacency.Analyze([Box("bez čp/če", -5, -5, 5, 5), Box("bez čp/če", 5, -5, 15, 5)], null).Position);

    [Fact]
    public void Analyze_PinInsideChurchOrOffice_IsInconclusive()
    {
        var verdict = HouseAdjacency.Analyze([Box("č.p. 1", -10, -10, 10, 10, "PublicServices"), Box("č.p. 2", 12, -5, 20, 5)], null);

        Assert.Null(verdict.Position);
        Assert.Contains("střed obce", verdict.Reason);
    }

    [Fact]
    public void Analyze_PinBetweenTwoSeparateHouses_IsInconclusive()
    {
        var verdict = HouseAdjacency.Analyze([Box("č.p. 1", -12, -5, -2, 5), Box("č.p. 2", 2.5, -5, 12, 5)], null);

        Assert.Null(verdict.Position);
        Assert.Contains("mezi budovami", verdict.Reason);
    }

    [Fact]
    public void Analyze_PinOnPavementInFrontOfHouse_UsesNearestHouse()
    {
        var verdict = HouseAdjacency.Analyze([Box("č.p. 1", -5, 2, 5, 12), Box("č.p. 2", 5, 2, 15, 12)], null);

        Assert.Equal("č.p. 1", verdict.BuildingName);
        Assert.Equal(HousePositions.SemiDetached, verdict.Position);
    }

    [Fact]
    public void Analyze_NoBuildingsOrFarAway_IsInconclusive()
    {
        Assert.Null(HouseAdjacency.Analyze([], null).Position);
        Assert.Null(HouseAdjacency.Analyze([Box("č.p. 1", 20, 20, 30, 30)], null).Position);
    }

    [Theory]
    [InlineData("Znojmo", new[] { "Znojmo", "Přímětice" }, true)]
    [InlineData("Znojmo - Přímětice", new[] { "Znojmo" }, true)]
    [InlineData("stitary", new[] { "Štítary" }, true)]
    [InlineData(null, new[] { "Štítary" }, true)]
    [InlineData("Kubšice", new string[0], true)]
    [InlineData("Kubšice", new[] { "Olbramovice" }, false)]
    public void MunicipalityMatches_IgnoresDiacriticsAndParts(string? municipality, string[] names, bool expected)
        => Assert.Equal(expected, HouseAdjacency.MunicipalityMatches(municipality, names));

    [Fact]
    public void PromptFacts_NameTheBuildingAndWalls()
    {
        var verdict = HouseAdjacency.Analyze(Fixture("drnholec", 48.8639383398262, 16.4921431274455), "Drnholec");

        var facts = verdict.PromptFacts();
        Assert.Contains("č.p. 334", facts);
        Assert.Contains("č.p. 335", facts);
        Assert.Contains("m of shared wall", facts);
        Assert.Contains("no residential building", new CadastreVerdict(null, "x").PromptFacts());
    }
}
