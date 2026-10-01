using RealEstate.Api.Contracts.Listings;

namespace RealEstate.Api.Services.Duplicates;

/// <summary>Sloučený pohled na tutéž nemovitost ve všech zdrojích (primární + duplikáty).</summary>
public interface IDuplicateGroupService
{
    /// <summary>
    /// Vrátí skupinu, do které inzerát patří. Null, když inzerát neexistuje.
    /// Bez duplikátů vrací skupinu s jedinou položkou (UI sekci skryje).
    /// </summary>
    Task<DuplicateGroupDto?> GetGroupAsync(Guid listingId, CancellationToken ct);

    /// <summary>
    /// Fotky pro detail, klasifikaci, analýzu a export: nejúplnější sada ve skupině duplicit.
    /// Bazoš má max 20 fotek, Sreality 49 – z ořezané sady se analýza dělat nedá.
    /// Bez skupiny vrací vlastní fotky inzerátu.
    /// </summary>
    Task<GroupPhotoSet> GetGroupPhotoSetAsync(Guid listingId, CancellationToken ct);
}

/// <summary>Sada fotek vybraná pro inzerát; <see cref="IsBorrowed"/> = pochází od jiného člena skupiny.</summary>
public sealed record GroupPhotoSet(
    Guid OwnerListingId,
    string OwnerSourceCode,
    IReadOnlyList<RealEstate.Domain.Entities.ListingPhoto> Photos,
    bool IsBorrowed);
