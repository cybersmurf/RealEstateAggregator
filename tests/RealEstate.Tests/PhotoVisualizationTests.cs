using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Vizualizace (render) v inzerátu – Lechovice 5+1 (1. 10. 2026): rendery obýváku a kuchyně
//  prošly klasifikací jako skutečné místnosti a zkreslily hodnocení stavu.
// ─────────────────────────────────────────────────────────────────
public class PhotoVisualizationTests
{
    [Fact]
    public void Flag_AddsLabel_ClearsDamage_PrefixesDescription()
    {
        var c = new PhotoClassificationService.PhotoClassificationJson
        {
            Category = "living_room", Labels = ["fireplace"], IsVisualization = true,
            DamageDetected = true, DamageEvidence = "crack on wall", Description = "Obývací pokoj s krbem.",
        };

        PhotoClassificationService.ApplyVisualizationFlag(c);

        Assert.Equal(["visualization", "fireplace"], c.Labels);
        Assert.False(c.DamageDetected);
        Assert.Null(c.DamageEvidence);
        Assert.StartsWith("Vizualizace: ", c.Description);
    }

    [Fact]
    public void LabelOnly_WithoutFlag_IsAlsoVisualization()
    {
        var c = new PhotoClassificationService.PhotoClassificationJson { Category = "kitchen", Labels = ["visualization"], Description = "Vizualizace: kuchyně" };

        PhotoClassificationService.ApplyVisualizationFlag(c);

        Assert.True(c.IsVisualization);
        Assert.Single(c.Labels!);
        Assert.Equal("Vizualizace: kuchyně", c.Description);
    }

    [Fact]
    public void RealPhoto_Unchanged()
    {
        var c = new PhotoClassificationService.PhotoClassificationJson { Category = "bathroom", Labels = ["mold"], DamageDetected = true, Description = "Plíseň v rohu." };

        PhotoClassificationService.ApplyVisualizationFlag(c);

        Assert.False(c.IsVisualization);
        Assert.True(c.DamageDetected);
        Assert.Equal("Plíseň v rohu.", c.Description);
    }

    [Fact]
    public void Validator_NeverConfirmsDamageOnRender()
    {
        Assert.False(PhotoDamageValidator.IsConfirmed(true, ["visualization", "crack"], "damage", "crack in the wall", "crack"));
        Assert.True(PhotoDamageValidator.IsConfirmed(true, ["crack"], "damage", "crack in the wall", "crack"));
    }
}
