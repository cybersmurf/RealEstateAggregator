using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  PlausiblePricePerM2 – Kč/m² je hlavní vstup cenového signálu.
//  Reálné případy ze screenshotu (dům v Lechovicích za 7 300 000 Kč):
//    • BAZOS měl v ploše kuchyň (10 m²)  → 730 000 Kč/m² → "Nadhodnocená"
//    • SREALITY měla v ploše pozemek (1238 m²) → 5 897 Kč/m² → "Přiměřená"
//  Skutečná plocha domu je 129 m², tj. 56 589 Kč/m². Ani jeden signál neplatil.
// ─────────────────────────────────────────────────────────────────
public class PriceSignalPlausibilityTests
{
    private static decimal? ForHouse(decimal? price, double? builtUp, double? land = null)
        => OllamaTextService.PlausiblePricePerM2(price, builtUp, land, isLand: false);

    [Fact]
    public void KitchenAreaMistakenForHouse_IsRejected()
    {
        Assert.Null(ForHouse(7_300_000m, 10));
    }

    [Fact]
    public void LandAreaMistakenForBuiltUp_IsRejected()
    {
        Assert.Null(ForHouse(7_300_000m, 1238));
    }

    [Fact]
    public void CorrectArea_IsAccepted()
    {
        Assert.Equal(56_589m, ForHouse(7_300_000m, 129)!.Value, 0);
    }

    [Theory]
    [InlineData(5_500_000, 106, true)]   // ~52 000 Kč/m² – běžný dům
    [InlineData(2_000_000, 100, true)]   // 20 000 Kč/m² – levný venkov
    [InlineData(600_000, 100, true)]     // 6 000 Kč/m² – spodní mez, zchátralý dům
    [InlineData(599_000, 100, false)]    // pod mezí → skoro jistě chyba v ploše
    [InlineData(25_000_000, 100, true)]  // 250 000 Kč/m² – horní mez
    [InlineData(25_100_000, 100, false)] // nad mezí
    public void PlausibilityBounds(decimal price, double area, bool expectValue)
    {
        var result = ForHouse(price, area);
        Assert.Equal(expectValue, result is not null);
    }

    [Fact]
    public void FallsBackToLandAreaWhenBuiltUpMissing()
    {
        Assert.Equal(50_000m, ForHouse(10_000_000m, null, 200)!.Value, 0);
    }

    [Fact]
    public void LandListing_UsesLowerScale()
    {
        // Pozemek za 1 200 Kč/m² je normální, u budovy by to znamenalo chybu v datech
        var land = OllamaTextService.PlausiblePricePerM2(1_200_000m, null, 1000, isLand: true);
        Assert.Equal(1_200m, land!.Value, 0);

        Assert.Null(ForHouse(1_200_000m, null, 1000));
    }

    [Fact]
    public void MissingOrZeroInputs_ReturnNull()
    {
        Assert.Null(ForHouse(null, 100));
        Assert.Null(ForHouse(0m, 100));
        Assert.Null(ForHouse(5_000_000m, null));
        Assert.Null(ForHouse(5_000_000m, 0));
        Assert.Null(ForHouse(5_000_000m, -50));
    }
}

// ─────────────────────────────────────────────────────────────────
//  Regresní pojistka: bez věrohodné Kč/m² se signál nesmí počítat.
//
//  27. 8. 2026 migrace smazala 1 108 signálů postavených na špatných
//  vstupech, ale bulk job jich 394 hned vrátil zpátky — PlausiblePricePerM2
//  totiž jen vynechávala Kč/m² z promptu, inzerát ale stejně šel do modelu
//  a ten vrátil verdikt z ceny samotné. 325 z nich nemělo plochu vůbec.
// ─────────────────────────────────────────────────────────────────
public class PriceSignalSkipTests
{
    [Theory]
    // žádná plocha → nelze spočítat
    [InlineData(7_300_000, null, null, false)]
    [InlineData(7_300_000, 0d, 0d, false)]
    // plocha z kuchyně (10 m²) → 730 000 Kč/m², nesmysl
    [InlineData(7_300_000, 10d, null, false)]
    // plocha z pozemku (1 238 m²) → 5 897 Kč/m², pod dolní mezí
    [InlineData(7_300_000, 1_238d, null, false)]
    // skutečná plocha domu 129 m² → 56 589 Kč/m², v pořádku
    [InlineData(7_300_000, 129d, null, true)]
    public void Budova_DostaneSignal_JenPriVerohodneCeneZaM2(
        decimal price, double? areaBuiltUp, double? areaLand, bool expectSignal)
    {
        var perM2 = OllamaTextService.PlausiblePricePerM2(price, areaBuiltUp, areaLand, isLand: false);

        Assert.Equal(expectSignal, perM2 is not null);
    }

    [Fact]
    public void Pozemek_MaVlastniMez()
    {
        // 2 000 Kč/m² je u pozemku běžné, u budovy by to spadlo pod dolní mez
        Assert.NotNull(OllamaTextService.PlausiblePricePerM2(2_000_000m, 1_000d, null, isLand: true));
        Assert.Null(OllamaTextService.PlausiblePricePerM2(2_000_000m, 1_000d, null, isLand: false));
    }
}
