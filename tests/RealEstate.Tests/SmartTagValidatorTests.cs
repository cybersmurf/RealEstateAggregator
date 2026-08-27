using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  SmartTagValidator – pojistka proti halucinovaným tagům
//  Reálný případ (BAZOS 222122556): 60 let starý dům "vhodný k modernizaci"
//  dostal od modelu tagy ["cihlový dům","novostavba","terasa","zahrada","kolaudovaný"].
// ─────────────────────────────────────────────────────────────────
public class SmartTagValidatorTests
{
    private const string RealTitle = "Prodej RD 4+1, 106 m², pozemek 718 m² – Znojmo, Oblekovice";

    private const string RealDescription = """
        Prodám rodinný dům 4+1 (106 m²) na pozemku 718 m², Znojmo – Oblekovice, ul. Nesachlebská 190/26.
        Cena 5 500 000 Kč (dle znaleckého posudku).
        Přízemní nepodsklepený dům, sedlová střecha, jednogenerační byt 4+1 se dvěma kuchyněmi.
        Vedlejší hospodářská stavba, vlastní studna (nevyužívaná). Stáří cca 60 let, průběžně
        udržovaný a částečně rekonstruovaný, průměrný technický stav – vhodný k modernizaci.
        Napojen na elektřinu, obecní vodovod, kanalizaci, plyn. Ústřední topení plynovým kotlem.
        """;

    private static List<string> Filter(params string[] tags)
        => SmartTagValidator.Filter(tags, RealTitle, RealDescription);

    [Fact]
    public void RealCase_DropsAllFabricatedTags()
    {
        var result = Filter("cihlový dům", "novostavba", "terasa", "zahrada", "kolaudovaný");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("novostavba")]   // dům je 60 let starý
    [InlineData("terasa")]       // v popisu není
    [InlineData("kolaudovaný")]  // v popisu není
    [InlineData("zahrada")]      // popis mluví o pozemku, ne o zahradě
    [InlineData("bazén")]
    [InlineData("výtah")]
    public void UnsupportedTag_IsDropped(string tag)
    {
        Assert.Empty(Filter(tag));
    }

    [Theory]
    [InlineData("rekonstrukce")]  // "částečně rekonstruovaný"
    [InlineData("studna")]        // "vlastní studna"
    [InlineData("plyn")]          // "plyn", "plynovým kotlem"
    [InlineData("kanalizace")]    // "kanalizaci"
    public void SupportedTag_IsKept(string tag)
    {
        Assert.Equal([tag], Filter(tag));
    }

    [Fact]
    public void NegatedWord_DoesNotSupportTag()
    {
        // "nepodsklepený" obsahuje podřetězec "sklep" – porovnání po slovech to musí ustát,
        // jinak dostane dům bez sklepa tag "sklep"
        Assert.Empty(Filter("sklep"));
    }

    [Fact]
    public void GenericWordAlone_DoesNotSupportCompoundTag()
    {
        // "cihlový dům" nesmí projít jen proto, že se v popisu vyskytuje slovo "dům"
        Assert.Empty(Filter("cihlový dům"));
    }

    [Fact]
    public void CompoundTag_KeptWhenContentWordMatches()
    {
        Assert.Equal(["sedlová střecha"], Filter("sedlová střecha"));
    }

    [Fact]
    public void MatchIsDiacriticsInsensitive()
    {
        // Popis má "rekonstruovaný", tag bez diakritiky musí projít
        Assert.Equal(["rekonstrukce"], SmartTagValidator.Filter(
            ["rekonstrukce"], null, "Dum po castecne rekonstrukci."));
    }

    [Fact]
    public void PreservesOrderAndRemovesDuplicates()
    {
        var result = Filter("studna", "novostavba", "plyn", "studna");

        Assert.Equal(["studna", "plyn"], result);
    }

    [Fact]
    public void EmptyAndWhitespaceTags_AreIgnored()
    {
        Assert.Empty(SmartTagValidator.Filter(["", "   "], RealTitle, RealDescription));
    }

    [Fact]
    public void NoSourceText_DropsEverything()
    {
        Assert.Empty(SmartTagValidator.Filter(["sklep", "zahrada"], null, null));
    }

    [Fact]
    public void PodsklepenyHouse_KeepsSklepTag()
    {
        // Česká předpona schová kmen: "podsklepený" znamená, že sklep JE
        var result = SmartTagValidator.Filter(
            ["sklep"], "Prodej domu", "Zděný podsklepený dům se sedlovou střechou.");

        Assert.Equal(["sklep"], result);
    }

    [Fact]
    public void NepodsklepenyHouse_StillDropsSklepTag()
    {
        // Negace se nesmí chytit na alias "podsklep"
        var result = SmartTagValidator.Filter(
            ["sklep"], "Prodej domu", "Přízemní nepodsklepený dům, sedlová střecha.");

        Assert.Empty(result);
    }

    [Fact]
    public void GenuineNewBuild_KeepsNovostavbaTag()
    {
        // Kontrola, že pojistka nezahazuje pravdivé tagy
        var result = SmartTagValidator.Filter(
            ["novostavba", "terasa"],
            "Prodej novostavby 5+kk",
            "Novostavba rodinného domu, kolaudace 2025, terasa 20 m² orientovaná na jih.");

        Assert.Equal(["novostavba", "terasa"], result);
    }
}
