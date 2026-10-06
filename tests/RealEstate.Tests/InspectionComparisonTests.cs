using RealEstate.Api.Services;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// Porovnání fotek z inzerátu s fotkami z prohlídky – výběr vzorku, čtení odpovědi modelu a zpráva.
public class InspectionComparisonTests
{
    [Fact]
    public void PickEvenly_FewItems_ReturnsAll()
        => Assert.Equal([1, 2, 3], InspectionComparisonService.PickEvenly([1, 2, 3], 4));

    [Fact]
    public void PickEvenly_ManyItems_SpreadsAcrossWholeList()
        => Assert.Equal([0, 5, 10, 15], InspectionComparisonService.PickEvenly(Enumerable.Range(0, 20).ToList(), 4));

    [Fact]
    public void PickForViewing_PutsDamagedPhotosFirst()
    {
        var photos = Enumerable.Range(0, 12)
            .Select(i => new UserListingPhoto { Id = Guid.NewGuid(), OriginalFileName = $"IMG_{i}.jpg", DamageDetected = i is 7 or 9 })
            .ToList();

        var picked = InspectionComparisonService.PickForViewing(photos, 6);

        Assert.Equal(6, picked.Count);
        Assert.Equal(["IMG_7.jpg", "IMG_9.jpg"], picked.Take(2).Select(p => p.OriginalFileName));
        Assert.Equal(6, picked.Distinct().Count());
    }

    [Fact]
    public void ParseComparison_ReadsFindings_AndNormalizesUnknownValues()
    {
        var parsed = InspectionComparisonService.ParseComparison("""
            {"same_place":true,"summary":"Koupelna je ve skutečnosti tmavší.","findings":[
              {"type":"hidden_defect","severity":"high","listing_photo":1,"viewing_photo":3,"description":"U vany je plíseň, kterou inzerát nezabírá."},
              {"type":"Brightened","severity":"extreme","listing_photo":2,"viewing_photo":null,"description":"Fotka je výrazně přesvětlená."},
              {"type":"made_up","severity":"low","description":"Jiné dlaždice."},
              {"type":"staged","severity":"low","description":"  "}
            ]}
            """);

        Assert.NotNull(parsed);
        Assert.Equal("Koupelna je ve skutečnosti tmavší.", parsed.Value.Summary);
        Assert.Equal(3, parsed.Value.Findings.Count);
        Assert.Equal(new PhotoFinding("hidden_defect", "high", "U vany je plíseň, kterou inzerát nezabírá.", 1, 3), parsed.Value.Findings[0]);
        Assert.Equal(("brightened", "low", (int?)null), (parsed.Value.Findings[1].Type, parsed.Value.Findings[1].Severity, parsed.Value.Findings[1].InspectionPhoto));
        Assert.Equal("other", parsed.Value.Findings[2].Type);
    }

    [Fact]
    public void ParseComparison_DifferentPlace_HasNoFindings()
    {
        var parsed = InspectionComparisonService.ParseComparison(
            """{"same_place":false,"summary":"Jiná místnost.","findings":[{"type":"staged","severity":"low","description":"Jiný nábytek."}]}""");

        Assert.NotNull(parsed);
        Assert.Empty(parsed.Value.Findings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("omlouvám se, nemohu")]
    [InlineData("[1,2]")]
    public void ParseComparison_UnusableAnswer_IsNull(string? raw)
        => Assert.Null(InspectionComparisonService.ParseComparison(raw));

    [Fact]
    public void SummarizeByType_CountsFindingsAndHouses()
    {
        var (houseA, houseB) = (Guid.NewGuid(), Guid.NewGuid());
        var summary = InspectionComparisonService.SummarizeByType(
        [
            (houseA, new PhotoFinding("wide_angle", "medium", "a", null, null)),
            (houseA, new PhotoFinding("wide_angle", "low", "b", null, null)),
            (houseB, new PhotoFinding("wide_angle", "medium", "c", null, null)),
            (houseB, new PhotoFinding("hidden_defect", "high", "d", null, null)),
        ]);

        Assert.Equal(new FindingTypeCountDto("wide_angle", "Širokoúhlý záběr zvětšuje prostor", 3, 2, 0), summary[0]);
        Assert.Equal(new FindingTypeCountDto("hidden_defect", "Vada, kterou inzerát neukazuje", 1, 1, 1), summary[1]);
    }

    [Fact]
    public void BuildReport_ListsFindingsBySeverity_AndOmittedParts()
    {
        var report = InspectionComparisonService.BuildReport("Prodej rodinného domu 210 m²",
        [
            new PhotoComparisonDto("bathroom", "Koupelna a WC", 2, 4, "Koupelna je ve skutečnosti tmavší.",
            [
                new PhotoFinding("brightened", "low", "Fotka je přesvětlená.", 1, null),
                new PhotoFinding("hidden_defect", "high", "U vany je plíseň.", null, 3),
            ], DateTime.UtcNow),
            new PhotoComparisonDto("basement", "Sklep", 0, 12, "Inzerát tuto část vůbec neukazuje, na prohlídce jste ji vyfotili 12×.",
            [
                new PhotoFinding("omitted", "medium", "Sklep: v inzerátu není ani jedna fotka.", null, null),
            ], DateTime.UtcNow),
        ], ["Garáž a dílna"], new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc));

        Assert.Contains("# Inzerát vs. prohlídka – porovnání fotek", report);
        Assert.Contains("- **Vada, kterou inzerát neukazuje:** 1× (z toho 1× závažné)", report);
        Assert.True(report.IndexOf("U vany je plíseň.", StringComparison.Ordinal) < report.IndexOf("Fotka je přesvětlená.", StringComparison.Ordinal));
        Assert.Contains("V inzerátu žádná fotka, z prohlídky 12.", report);
        Assert.Contains("Na prohlídce jste nevyfotili: Garáž a dílna.", report);
    }

    [Fact]
    public void BuildReport_NoFindings_SaysListingMatches()
    {
        var report = InspectionComparisonService.BuildReport("Dům",
            [new PhotoComparisonDto("kitchen", "Kuchyň", 2, 3, "Inzerát odpovídá skutečnosti.", [], DateTime.UtcNow)],
            [], DateTime.UtcNow);

        Assert.Contains("model nenašel rozdíl", report);
    }
}
