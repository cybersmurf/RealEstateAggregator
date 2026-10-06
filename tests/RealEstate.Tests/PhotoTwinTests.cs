using RealEstate.Api.Services;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// „Dvojčata" v galerii: stejný záběr, jiný interiér = retuš, virtuální zařízení nebo vizualizace.
public class PhotoTwinTests
{
    private static ListingPhoto Photo(int order, string? labels = null, string? description = null, bool damage = false)
        => new() { Id = Guid.NewGuid(), Order = order, PhotoLabels = labels, PhotoDescription = description, DamageDetected = damage };

    [Fact]
    public void Chunk_SmallGroup_IsOneBatch()
        => Assert.Equal([[1, 2, 3]], PhotoTwinService.Chunk([1, 2, 3], 8, 2));

    [Fact]
    public void Chunk_LargeGroup_OverlapsSoNeighboursMeet()
    {
        var chunks = PhotoTwinService.Chunk(Enumerable.Range(1, 15).ToList(), 8, 2);

        Assert.Equal(3, chunks.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], chunks[0]);
        Assert.Equal([7, 8, 9, 10, 11, 12, 13, 14], chunks[1]);
        Assert.Equal([13, 14, 15], chunks[2]);
    }

    [Fact]
    public void ParseTwins_KeepsValidPairs_DropsNonsense()
    {
        var twins = PhotoTwinService.ParseTwins("""
            {"twins":[
              {"a":1,"b":3,"edited":3,"reason":"Stejný pohled na obývací pokoj, na druhé fotce je jiný nábytek."},
              {"a":2,"b":2,"edited":2,"reason":"tatáž fotka"},
              {"a":4,"b":9,"edited":4,"reason":"mimo rozsah"},
              {"a":2,"b":4,"edited":7,"reason":"upravená neznámá"}
            ]}
            """, count: 4);

        Assert.Equal(2, twins.Count);
        Assert.Equal(new PhotoTwin(1, 3, 3, "Stejný pohled na obývací pokoj, na druhé fotce je jiný nábytek."), twins[0]);
        Assert.Null(twins[1].Edited);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nic jsem nenašel")]
    [InlineData("""{"twins":[]}""")]
    [InlineData("""{"pairs":[{"a":1,"b":2}]}""")]
    public void ParseTwins_NothingUsable_IsEmpty(string? raw)
        => Assert.Empty(PhotoTwinService.ParseTwins(raw, 5));

    [Fact]
    public void MarkTwin_EditedPhoto_GetsVisualizationLabel_AndLosesDamageFlag()
    {
        var original = Photo(order: 2, labels: """["fireplace"]""", description: "Obývací pokoj s krbem.");
        var edited = Photo(order: 3, description: "Obývací pokoj s krbem a novou sedačkou.", damage: true);

        PhotoTwinService.MarkTwin(original, edited, isEdited: false, "Stejný záběr, jiný nábytek.");
        PhotoTwinService.MarkTwin(edited, original, isEdited: true, "Stejný záběr, jiný nábytek.");

        Assert.Equal("""["fireplace","twin"]""", original.PhotoLabels);
        Assert.Equal("Obývací pokoj s krbem.\nDvojče fotky č. 4: Stejný záběr, jiný nábytek.", original.PhotoDescription);
        Assert.Equal("""["visualization","twin"]""", edited.PhotoLabels);
        Assert.EndsWith("Dvojče fotky č. 3 – tahle verze je upravená: Stejný záběr, jiný nábytek.", edited.PhotoDescription);
        Assert.False(edited.DamageDetected);
    }

    [Fact]
    public void ClearTwinMarks_RemovesTwinLabelAndNote_KeepsTheRest()
    {
        var photo = Photo(order: 0, labels: """["visualization","twin"]""",
            description: "Vizualizace: obývací pokoj.\nDvojče fotky č. 2 – tahle verze je upravená: jiný nábytek.");

        PhotoTwinService.ClearTwinMarks(photo);

        Assert.Equal("""["visualization"]""", photo.PhotoLabels);
        Assert.Equal("Vizualizace: obývací pokoj.", photo.PhotoDescription);
    }
}
