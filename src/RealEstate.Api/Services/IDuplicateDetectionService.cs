using RealEstate.Api.Contracts.Listings;

namespace RealEstate.Api.Services;

/// <summary>
/// Detekce stejné nemovitosti napříč zdroji (SREALITY + BAZOS + REMAX = 1 dům, 3 záznamy).
/// Zapisuje <c>listings.duplicate_of_listing_id</c> – sloupec existoval od května 2026
/// (commit fd62dee), ale nic ho neplnilo, takže se stejný dům zobrazoval vícekrát
/// s protichůdnými AI cenovými signály.
/// </summary>
public interface IDuplicateDetectionService
{
    /// <summary>Přepočítá duplicitní vazby nad všemi aktivními inzeráty. Idempotentní – každé volání staví vazby znovu.</summary>
    Task<DuplicateScanResultDto> DetectAsync(CancellationToken cancellationToken);
}
