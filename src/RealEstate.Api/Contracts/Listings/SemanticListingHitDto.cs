namespace RealEstate.Api.Contracts.Listings;

/// <summary>Výsledek sémantického hledání: inzerát + kosinová podobnost dotazu (0–1).</summary>
public sealed record SemanticListingHitDto(double Similarity, ListingSummaryDto Listing);
