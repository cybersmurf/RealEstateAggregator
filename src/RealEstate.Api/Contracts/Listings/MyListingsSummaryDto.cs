namespace RealEstate.Api.Contracts.Listings;

/// <summary>
/// Souhrn inzerátů tagovaných uživatelem, seskupených dle stavu.
/// </summary>
public sealed class MyListingsSummaryDto
{
    /// <summary>Počty inzerátů dle stavu (pouze stavy s alespoň 1 inzerátem).</summary>
    public Dictionary<string, int> CountsByStatus { get; set; } = new();

    /// <summary>Inzeráty seskupené dle stavu, seřazené dle priority stavu.</summary>
    public List<UserListingsGroupDto> Groups { get; set; } = new();
}

/// <summary>
/// Skupina inzerátů pro jeden uživatelský stav.
/// </summary>
public sealed class UserListingsGroupDto
{
    public string Status { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<ListingSummaryDto> Listings { get; set; } = new();
}
