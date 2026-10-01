using RealEstate.Api.Services.Duplicates;
using static RealEstate.Api.Services.Duplicates.DuplicateGroupService;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Fotky skupiny duplicit: Bazoš 20 fotek (limit portálu) vs. Sreality 49 –
//  detail, klasifikace, analýza i export mají jet nad nejúplnější sadou.
// ─────────────────────────────────────────────────────────────────
public class GroupPhotoOwnerTests
{
    private static readonly Guid Sreality = Guid.NewGuid();
    private static readonly Guid Bazos = Guid.NewGuid();
    private static readonly Guid Idnes = Guid.NewGuid();

    private static PhotoOwnerCandidate Member(Guid id, string code, int photos, int daysOld = 0, bool active = true)
        => new(id, code, photos, new DateTime(2026, 9, 6).AddDays(-daysOld), active);

    [Fact]
    public void MostPhotosWins_EvenWhenNotCurrentNorPrimary()
    {
        var members = new[] { Member(Bazos, "BAZOS", 20), Member(Sreality, "SREALITY", 49), Member(Idnes, "IDNES", 16) };

        Assert.Equal(Sreality, ChoosePhotoOwner(Bazos, Bazos, members));
    }

    [Fact]
    public void Tie_PrefersCurrentListing_ThenPrimary()
    {
        var members = new[] { Member(Sreality, "SREALITY", 49), Member(Bazos, "REALITYMIX", 49) };

        Assert.Equal(Bazos, ChoosePhotoOwner(Bazos, Sreality, members));
        Assert.Equal(Sreality, ChoosePhotoOwner(Idnes, Sreality, members));
    }

    [Fact]
    public void InactiveMember_UsedOnlyWhenNoActiveHasPhotos()
    {
        var members = new[] { Member(Sreality, "SREALITY", 49, active: false), Member(Bazos, "BAZOS", 20) };
        Assert.Equal(Bazos, ChoosePhotoOwner(Bazos, Sreality, members));

        var onlyInactive = new[] { Member(Sreality, "SREALITY", 49, active: false), Member(Bazos, "BAZOS", 0) };
        Assert.Equal(Sreality, ChoosePhotoOwner(Bazos, Sreality, onlyInactive));
    }

    [Fact]
    public void NoPhotosAnywhere_ReturnsListingItself()
    {
        var members = new[] { Member(Sreality, "SREALITY", 0), Member(Bazos, "BAZOS", 0) };

        Assert.Equal(Bazos, ChoosePhotoOwner(Bazos, Sreality, members));
    }

    [Fact]
    public void SingleListing_ReturnsItself()
        => Assert.Equal(Bazos, ChoosePhotoOwner(Bazos, Bazos, [Member(Bazos, "BAZOS", 20)]));
}
