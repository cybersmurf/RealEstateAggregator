namespace RealEstate.Api.Services;

/// <summary>
/// Hodnoty listings.geocode_source, které znamenají PŘIBLIŽNOU polohu (střed obce nebo její části),
/// ne polohu domu. Takový bod se nepoužije pro polohu domu ani jako důkaz shody v detekci duplicit.
/// „sreality-area“ od 7. 11. 2026: Sreality u inzerátu bez adresy posílá bod obce
/// (locality.entity_type municipality/ward/quarter) – developer v Tasovicích tak měl pět domů
/// na jednom „špendlíku“.
/// </summary>
public static class GpsPrecision
{
    public const string Nominatim = "nominatim";
    public const string SrealityArea = "sreality-area";

    public static bool IsApproximate(string? geocodeSource) =>
        geocodeSource is Nominatim or SrealityArea;
}
