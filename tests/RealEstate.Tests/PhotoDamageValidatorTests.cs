using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  PhotoDamageValidator – pojistka proti falešnému damage_detected
//  Reálný případ (LEXAMO, RD 4+kk Dyje): dvůr s prolézačkou a letecký snímek
//  dostaly damage_detected=true jen se štítky renovation_needed/brick_walls/wooden_beams.
// ─────────────────────────────────────────────────────────────────
public class PhotoDamageValidatorTests
{
    private const string GoodConditionDescription =
        "The image depicts a single-story house with a red-tiled roof and light-colored exterior walls. " +
        "The house appears to be in good condition, with no visible defects.";

    [Fact]
    public void RealCase_RenovationNeededWithGoodConditionDescription_IsNotDamage()
    {
        var result = PhotoDamageValidator.IsConfirmed(
            true, ["renovation_needed", "wooden_beams", "brick_walls", "garden"], "exterior",
            GoodConditionDescription);

        Assert.False(result);
    }

    [Theory]
    [InlineData("mold")]
    [InlineData("water_damage")]
    [InlineData("crack")]
    [InlineData("broken_windows")]
    [InlineData("damaged_roof")]
    [InlineData(" Crack ")]
    public void DamageLabel_ConfirmsFlag(string label)
    {
        Assert.True(PhotoDamageValidator.IsConfirmed(true, ["garden", label], "exterior", null));
    }

    [Fact]
    public void DamageCategory_ConfirmsFlagWithoutLabels()
    {
        Assert.True(PhotoDamageValidator.IsConfirmed(true, null, "damage", null));
    }

    [Theory]
    [InlineData("A two-story house with a weathered exterior, featuring peeling paint and visible cracks on the walls.")]
    [InlineData("The walls and floor appear aged and damp, with visible water damage, mold, and discoloration.")]
    [InlineData("The roof appears to be in poor condition with visible damage.")]
    public void RealCase_DamageOnlyInDescription_ConfirmsFlag(string description)
    {
        Assert.True(PhotoDamageValidator.IsConfirmed(
            true, ["renovation_needed", "brick_walls"], "exterior", description));
    }

    [Theory]
    [InlineData("The house appears well-maintained with no visible damage or cracks.")]
    [InlineData("A renovated bathroom without any signs of mold; the tiles are new.")]
    [InlineData("A rotunda-style gazebo stands in the garden.")]
    public void NegatedOrUnrelatedWording_IsNotEvidence(string description)
    {
        Assert.False(PhotoDamageValidator.IsConfirmed(true, ["renovation_needed"], "exterior", description));
    }

    [Theory]
    [InlineData("peeling and missing plaster on the exterior wall")]
    [InlineData("exposed brickwork on the gable")]
    public void NamedEvidence_ConfirmsFlag_EvenWithCzechDescription(string evidence)
    {
        Assert.True(PhotoDamageValidator.IsConfirmed(
            true, ["renovation_needed"], "exterior", "Na fasádě je patrné poškození omítky.", evidence));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("N/A")]
    [InlineData("null")]
    [InlineData("  ")]
    [InlineData("no visible damage")]
    public void PlaceholderEvidence_IsNotEvidence(string evidence)
    {
        Assert.False(PhotoDamageValidator.IsConfirmed(
            true, ["renovation_needed"], "exterior", "Dům je v dobrém stavu.", evidence));
    }

    [Fact]
    public void FlagWithoutAnyEvidence_IsRejected()
    {
        Assert.False(PhotoDamageValidator.IsConfirmed(true, null, "interior", null));
        Assert.False(PhotoDamageValidator.IsConfirmed(true, [], "interior", ""));
    }

    [Fact]
    public void EvidenceWithoutFlag_StaysFalse()
    {
        Assert.False(PhotoDamageValidator.IsConfirmed(false, ["mold"], "bathroom", "Visible mold on the ceiling."));
    }
}
