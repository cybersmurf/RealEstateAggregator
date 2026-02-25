using System.Reflection;
using Pgvector;
using RealEstate.Api.Services;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  CosineSimilarity – private static v RagService (via reflection)
// ─────────────────────────────────────────────────────────────────
public class CosineSimilarityTests
{
    /// <summary>
    /// Zavolá private static RagService.CosineSimilarity(Vector, float[]) přes reflection.
    /// </summary>
    private static double Invoke(float[] a, float[] b)
    {
        var method = typeof(RagService).GetMethod(
            "CosineSimilarity",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (double)method.Invoke(null, [new Vector(a), b])!;
    }

    [Fact]
    public void IdenticalVectors_ReturnOne()
    {
        var v = new float[] { 1f, 2f, 3f };
        var result = Invoke(v, v);
        Assert.Equal(1.0, result, precision: 10);
    }

    [Fact]
    public void OrthogonalVectors_ReturnZero()
    {
        // [1,0,0] ⊥ [0,1,0] → cosine = 0
        var result = Invoke([1f, 0f, 0f], [0f, 1f, 0f]);
        Assert.Equal(0.0, result, precision: 10);
    }

    [Fact]
    public void OppositeVectors_ReturnNegativeOne()
    {
        // [1,0] a [-1,0] → cosine = -1
        var result = Invoke([1f, 0f], [-1f, 0f]);
        Assert.Equal(-1.0, result, precision: 10);
    }

    [Fact]
    public void ZeroVectorA_ReturnsZero_NoException()
    {
        // Nulový vektor → magA = 0 → ochrana proti dělení nulou
        var result = Invoke([0f, 0f, 0f], [1f, 2f, 3f]);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void ZeroVectorB_ReturnsZero_NoException()
    {
        var result = Invoke([1f, 2f, 3f], [0f, 0f, 0f]);
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void UnitVector_SimilarToSelf_IsOne()
    {
        var v = new float[] { 1f / MathF.Sqrt(3f), 1f / MathF.Sqrt(3f), 1f / MathF.Sqrt(3f) };
        var result = Invoke(v, v);
        Assert.Equal(1.0, result, precision: 5);
    }

    [Fact]
    public void HighDimensionalVectors_WorkCorrectly()
    {
        // Simulujeme embedding dimenzi 768 – všechna stejná čísla → cosine = 1
        var v = Enumerable.Repeat(0.1f, 768).ToArray();
        var result = Invoke(v, v);
        Assert.Equal(1.0, result, precision: 5);
    }

    [Fact]
    public void DifferentLengths_OnlyShorterUsed_NoException()
    {
        // Implementace iteruje do Math.Min(a.Length, b.Length) – různé délky nesmí hodit výjimku
        var ex = Record.Exception(() => Invoke([1f, 2f, 3f], [1f, 2f]));
        Assert.Null(ex);
    }
}

// ─────────────────────────────────────────────────────────────────
//  BuildListingText – private static v RagService (via reflection)
//  Strukturovaný text pro embedding (RAG kontextové vyhledávání)
// ─────────────────────────────────────────────────────────────────
public class BuildListingTextTests
{
    private static string Invoke(Listing listing)
    {
        var method = typeof(RagService).GetMethod(
            "BuildListingText",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [listing])!;
    }

    private static Listing MakeListing(Action<Listing>? configure = null)
    {
        var listing = new Listing
        {
            Id           = Guid.NewGuid(),
            SourceCode   = "TEST",
            SourceName   = "Test Source",
            Url          = "https://test.cz/1",
            Title        = "Rodinný dům 4+1 Znojmo",
            Description  = "Pěkný prostorný dům na okraji města s velkou zahradou.",
            LocationText = "Znojmo, Jihomoravský kraj",
            Municipality = "Znojmo",
            District     = "Znojmo",
            Region       = "Jihomoravský kraj",
            PropertyType = PropertyType.House,
            OfferType    = OfferType.Sale,
            Price        = 5_000_000m,
            AreaBuiltUp  = 150.0,
            AreaLand     = 600.0,
            Disposition  = "4+1",
            Condition    = "Dobrý",
            ConstructionType = "Cihla",
            FirstSeenAt  = DateTime.UtcNow,
        };
        configure?.Invoke(listing);
        return listing;
    }

    [Fact]
    public void Text_ContainsTitleAsHeading()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("# Rodinný dům 4+1 Znojmo", text);
    }

    [Fact]
    public void Text_ContainsPropertyTypeAndOfferType()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("House", text);
        Assert.Contains("Sale", text);
    }

    [Fact]
    public void Text_ContainsFormattedPrice()
    {
        var text = Invoke(MakeListing());
        // Cena musí být formátovaná s dekadickými skupinami
        Assert.Contains("5", text);   // "5 000 000 Kč" nebo "5,000,000"
        Assert.Contains("Kč", text);
    }

    [Fact]
    public void Text_ContainsLocation()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("Znojmo", text);
    }

    [Fact]
    public void Text_ContainsDisposition()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("4+1", text);
    }

    [Fact]
    public void Text_ContainsAreaAndLand()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("150", text);   // AreaBuiltUp
        Assert.Contains("600", text);   // AreaLand
    }

    [Fact]
    public void Text_ContainsDescription()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("zahradou", text);  // část popisu
        Assert.Contains("## Popis", text);
    }

    [Fact]
    public void Text_NullPrice_NoKcLine()
    {
        var text = Invoke(MakeListing(l => l.Price = null));
        // Pokud cena chybí, sekce "Cena:" se vůbec neobjeví
        Assert.DoesNotContain("Kč", text);
    }

    [Fact]
    public void Text_NullDescription_NoDescriptionSection()
    {
        var text = Invoke(MakeListing(l => { l.Description = null!; }));
        Assert.DoesNotContain("## Popis", text);
    }

    [Fact]
    public void Text_AuctionOfferType_SerializedAsAuction()
    {
        var text = Invoke(MakeListing(l => l.OfferType = OfferType.Auction));
        Assert.Contains("Auction", text);
    }

    [Fact]
    public void Text_ConditionAndConstructionType_Included()
    {
        var text = Invoke(MakeListing());
        Assert.Contains("Dobrý", text);
        Assert.Contains("Cihla", text);
    }
}
