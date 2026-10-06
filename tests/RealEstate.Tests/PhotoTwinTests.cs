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
              {"a":1,"b":3,"kind":"render","edited":3,"reason":"Stejný pohled na obývací pokoj, na druhé fotce je jiný nábytek."},
              {"a":2,"b":2,"kind":"render","edited":2,"reason":"tatáž fotka"},
              {"a":4,"b":9,"kind":"render","edited":4,"reason":"mimo rozsah"},
              {"a":2,"b":4,"kind":"Retouch","edited":7,"reason":"upravená neznámá"},
              {"a":1,"b":2,"kind":"duplicate","edited":null,"reason":"identické duplikáty leteckého snímku"},
              {"a":3,"b":4,"kind":"retake","edited":4,"reason":"před domem navíc parkuje auto"},
              {"a":1,"b":4,"edited":4,"reason":"bez druhu"}
            ]}
            """, count: 4);

        Assert.Equal(2, twins.Count);
        Assert.Equal(new PhotoTwin(1, 3, 3, "Stejný pohled na obývací pokoj, na druhé fotce je jiný nábytek.", "render"), twins[0]);
        Assert.Equal((2, 4, (int?)null, "retouch"), (twins[1].A, twins[1].B, twins[1].Edited, twins[1].Kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nic jsem nenašel")]
    [InlineData("""{"twins":[]}""")]
    [InlineData("""{"pairs":[{"a":1,"b":2}]}""")]
    [InlineData("""{"twins":[{"a":1,"b":2,"kind":"duplicate"}]}""")]
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
        Assert.EndsWith("Dvojče fotky č. 3 – tahle verze je vizualizace: Stejný záběr, jiný nábytek.", edited.PhotoDescription);
        Assert.False(edited.DamageDetected);
    }

    [Fact]
    public void MarkTwin_RetouchedPhoto_StaysAPhotograph()
    {
        var original = Photo(order: 0, description: "Stěna se skvrnou.", damage: true);
        var retouched = Photo(order: 1, description: "Čistá stěna.", damage: true);

        PhotoTwinService.MarkTwin(retouched, original, isEdited: true, "Skvrna na stěně chybí.", "retouch");

        Assert.Equal("""["twin"]""", retouched.PhotoLabels);
        Assert.EndsWith("Dvojče fotky č. 1 – tahle verze je retušovaná: Skvrna na stěně chybí.", retouched.PhotoDescription);
        Assert.True(retouched.DamageDetected);
    }

    [Fact]
    public void ClearTwinMarks_RemovesTwinLabelAndNote_KeepsClassifiersVisualization()
    {
        var photo = Photo(order: 0, labels: """["visualization","twin"]""",
            description: "Vizualizace: obývací pokoj.\nDvojče fotky č. 2 – tahle verze je vizualizace: jiný nábytek.");

        PhotoTwinService.ClearTwinMarks(photo);

        Assert.Equal("""["visualization"]""", photo.PhotoLabels);
        Assert.Equal("Vizualizace: obývací pokoj.", photo.PhotoDescription);
    }

    [Theory]
    [InlineData("vizualizace")]
    [InlineData("upravená")]   // formulace prvního běhu
    public void ClearTwinMarks_TakesBackVisualizationLabelItAddedItself(string wording)
    {
        var photo = Photo(order: 0, labels: """["visualization","garden","twin"]""",
            description: $"Průčelí domu s předzahrádkou.\nDvojče fotky č. 25 – tahle verze je {wording}: před domem parkuje auto.");

        PhotoTwinService.ClearTwinMarks(photo);

        Assert.Equal("""["garden"]""", photo.PhotoLabels);
        Assert.Equal("Průčelí domu s předzahrádkou.", photo.PhotoDescription);
    }
}
