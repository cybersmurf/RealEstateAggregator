using System.Text.Json;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  NormalizationValidator – ai_normalized_data smí tvrdit jen to, co je v textu.
//  Produkce 7. 10. 2026: energy_class bez zmínky o třídě u poloviny inzerátů,
//  výtah bez slova výtah u třetiny, tepelné čerpadlo bez „čerpadl" u 40 %.
// ─────────────────────────────────────────────────────────────────
public class NormalizationValidatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void NepodlozeneHodnotySeVynuluji()
    {
        const string json = """{"has_pool": true, "has_elevator": true, "has_garage": false, "heating_type": "heat_pump", "energy_class": "G", "year_built": 1985}""";
        var cleaned = NormalizationValidator.Clean(json,
            "Prodej rodinného domu 120 m²",
            "Dům se zahradou a venkovním bazénem, vytápění plynovým kotlem. Energetická náročnost zatím neznámá.",
            out var dropped);

        var root = Parse(cleaned);
        Assert.True(root.GetProperty("has_pool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("has_elevator").ValueKind);
        Assert.False(root.GetProperty("has_garage").GetBoolean());          // false se nechává
        Assert.Equal(JsonValueKind.Null, root.GetProperty("heating_type").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("energy_class").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("year_built").ValueKind);
        Assert.Equal(["has_elevator", "heating_type", "energy_class", "year_built"], dropped);
    }

    [Fact]
    public void PodlozeneHodnotyZustanou()
    {
        const string json = """{"has_elevator": true, "has_basement": true, "heating_type": "heat_pump", "energy_class": "B", "year_built": 2019}""";
        var cleaned = NormalizationValidator.Clean(json,
            "Prodej bytu 3+kk",
            "Byt v domě s výtahem, k bytu patří sklepní kóje. Vytápění tepelným čerpadlem, PENB třída B. Dům byl dokončen v roce 2019.",
            out var dropped);

        Assert.Empty(dropped);
        Assert.Equal(json, cleaned);
    }

    [Theory]
    [InlineData("Energetická třída C.", "C", true)]
    [InlineData("Průkaz energetické náročnosti: D", "D", true)]
    [InlineData("Energetický štítek budovy je G.", "G", true)]
    [InlineData("Třída A je v tomto případě jen název ulice Áčko.", "A", true)]
    [InlineData("Dům je po rekonstrukci.", "G", false)]
    [InlineData("Energetická náročnost zatím neznámá.", "G", false)]
    public void EnergetickaTridaMusiBytVTextuJmenovana(string description, string letter, bool kept)
    {
        var cleaned = NormalizationValidator.Clean($$"""{"energy_class": "{{letter}}"}""", "Prodej domu", description, out _);
        var value = Parse(cleaned).GetProperty("energy_class");
        Assert.Equal(kept, value.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public void BezDiakritikyAVelkychPismen()
    {
        var cleaned = NormalizationValidator.Clean("""{"has_terrace": true, "has_balcony": true}""",
            "PRODEJ BYTU S TERASOU", "K bytu nalezi lodzie.", out var dropped);
        Assert.Empty(dropped);
        Assert.Equal("""{"has_terrace": true, "has_balcony": true}""", cleaned);
    }

    [Fact]
    public void NevalidniJsonSeVraciBezeZmeny()
    {
        var cleaned = NormalizationValidator.Clean("not json", "a", "b", out var dropped);
        Assert.Equal("not json", cleaned);
        Assert.Empty(dropped);
    }
}
