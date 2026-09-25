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
}
