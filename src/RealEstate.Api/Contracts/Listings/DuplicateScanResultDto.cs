namespace RealEstate.Api.Contracts.Listings;

/// <summary>Výsledek běhu detekce duplikátů.</summary>
public sealed record DuplicateScanResultDto(
    int ActiveListings,
    int Clusters,
    int DuplicatesMarked);
