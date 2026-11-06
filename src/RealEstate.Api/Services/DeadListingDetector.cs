using System.Net;

namespace RealEstate.Api.Services;

/// <summary>
/// Pozná stažený inzerát i tam, kde zdroj nevrací 404/410.
/// Do 6. 11. 2026 kontrola mrtvých odkazů posílala jen HEAD a deaktivovala při 404/410 – každou noc
/// prošla ~770 zatuchlých inzerátů a nedeaktivovala žádný: Reality Čechy smazaný inzerát vrací 200
/// se štítkem „Nemovitost již není v nabídce“, RealityMIX 200 s „Požadovaný inzerát již není v naší
/// databázi“ a Bazoš přesměruje na výpis hledání (/inzerat/{id}/… → /inzeraty/…).
/// </summary>
public static class DeadListingDetector
{
    // Jen texty, které portál píše o OTEVŘENÉM inzerátu – ne obecná slova jako „prodáno“, která
    // stojí i u podobných nabídek pod detailem.
    private static readonly string[] GoneMarkers =
    [
        "smazany_inzerat",                              // Reality Čechy (třída bloku smazaného inzerátu)
        "Nemovitost již není v nabídce",                // Reality Čechy
        "Požadovaný inzerát již není v naší databázi",  // RealityMIX
    ];

    public static bool IsGoneStatus(HttpStatusCode status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.Gone;

    /// <summary>
    /// Přesměrování pryč z inzerátu: původní adresa nese id inzerátu, cílová už ne
    /// (Bazoš → výpis hledání). Kanonické přesměrování na adresu se stejným id (Sreality) je v pořádku.
    /// </summary>
    public static bool IsRedirectedAway(string originalUrl, string? finalUrl, string? externalId)
    {
        if (string.IsNullOrWhiteSpace(finalUrl) || string.IsNullOrWhiteSpace(externalId) || externalId.Length < 4)
            return false;
        if (string.Equals(originalUrl, finalUrl, StringComparison.OrdinalIgnoreCase))
            return false;
        return originalUrl.Contains(externalId, StringComparison.Ordinal)
            && !finalUrl.Contains(externalId, StringComparison.Ordinal);
    }

    public static bool HasGoneMarker(string? body) =>
        !string.IsNullOrEmpty(body)
        && GoneMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsDead(HttpStatusCode status, string originalUrl, string? finalUrl, string? externalId, string? body)
    {
        if (IsGoneStatus(status))
            return true;
        if (status != HttpStatusCode.OK)
            return false; // 403, 429, 5xx – nevíme, zkusíme příště
        return IsRedirectedAway(originalUrl, finalUrl, externalId) || HasGoneMarker(body);
    }
}
