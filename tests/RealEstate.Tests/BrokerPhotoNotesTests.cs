using RealEstate.Api.Services;

namespace RealEstate.Tests;

// FOTKY_OD_MAKLERE.md píše Claude Desktop při třídění fotek od makléře – formát je volný,
// parser musí přežít escapované podtržítka z Drivu i chybějící části
public class BrokerPhotoNotesTests
{
    private const string Lechovice = """
        # Fotky od makléře – Lechovice č. p. 96

        **Inzerát:** a0265389-cbf0-46ec-ba6e-688a3d070381
        **Zdroj:** Pavla Hájková (Coloseum), Úschovna 2. 10. 2026 – zásilky WR2M3 (půda, 5 ks) a WR5ID (hospodářské budovy, 22 ks)
        **Celkem:** 27 fotek, roztříděno do 11 kategorií (pojmenování: \<kategorie\>\_\<pořadí\>\_\_\<původní název\>)

        | Složka | Počet | Co je na fotkách |
        |---|---|---|
        | 01\_Podkrovi | 4 | Volné podkroví přes celou délku domu, krov z r. 2000 |
        | 05\_Kotel | 4 | Komora s kotlem BAXI ECO FOUR 1.24 (kombinovaný, 24 kW) |
        | 08\_Kulna\_a\_dilna | 2 | Dlouhý sklad/dílna; kůlna s plechovou střechou (ne eternit) |

        Poznámka: zařazení IMG\_8810 (dílna/sklad) a IMG\_8812 (dvůr u dřevníku) je odhad z náhledu.
        """;

    [Fact]
    public void Parse_ReadsDescriptionsByFolderName()
    {
        var parsed = BrokerPhotoNotes.Parse(Lechovice);

        Assert.Equal(3, parsed.Descriptions.Count);
        Assert.Equal("Komora s kotlem BAXI ECO FOUR 1.24 (kombinovaný, 24 kW)", parsed.Descriptions["05_Kotel"]);
        Assert.Equal("Dlouhý sklad/dílna; kůlna s plechovou střechou (ne eternit)", parsed.Descriptions["08_Kulna_a_dilna"]);
    }

    [Fact]
    public void Parse_ReadsSourceAndNotes()
    {
        var parsed = BrokerPhotoNotes.Parse(Lechovice);

        Assert.StartsWith("Pavla Hájková (Coloseum), Úschovna 2. 10. 2026", parsed.Source);
        Assert.Equal("Poznámka: zařazení IMG_8810 (dílna/sklad) a IMG_8812 (dvůr u dřevníku) je odhad z náhledu.", parsed.Notes);
    }

    [Fact]
    public void Parse_TableWithDifferentColumnOrder_StillFindsText()
    {
        var parsed = BrokerPhotoNotes.Parse("| Složka | Popis | Počet |\n|---|---|---|\n| 02_Schodiste | Dřevěné schody | 1 |\n");

        Assert.Equal("Dřevěné schody", parsed.Descriptions["02_Schodiste"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("# Jen nadpis bez tabulky")]
    public void Parse_WithoutTable_IsEmptyNotBroken(string? markdown)
    {
        var parsed = BrokerPhotoNotes.Parse(markdown);

        Assert.Empty(parsed.Descriptions);
        Assert.Null(parsed.Source);
    }

    [Theory]
    [InlineData("05_Kotel", "Kotel")]
    [InlineData("08_Kulna_a_dilna", "Kulna a dilna")]
    [InlineData("Dvur", "Dvur")]
    public void Label_StripsNumberAndUnderscores(string folder, string expected)
    {
        Assert.Equal(expected, BrokerPhotoNotes.Label(folder));
    }
}
