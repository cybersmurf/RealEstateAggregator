namespace RealEstate.Api.Contracts.Listings;

/// <param name="Checked">Počet inzerátů, u nichž proběhla HTTP kontrola.</param>
/// <param name="Deactivated">Počet inzerátů označených jako neaktivní (HTTP 404/410).</param>
public sealed record DeactivateDeadResult(int Checked, int Deactivated);
