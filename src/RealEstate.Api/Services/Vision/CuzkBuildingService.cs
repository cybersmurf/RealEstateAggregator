using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace RealEstate.Api.Services.Vision;

/// <summary>
/// Budova z katastru (INSPIRE Buildings ČÚZK). Půdorys je v místních metrech kolem GPS inzerátu:
/// x = na východ, y = na sever, počátek = GPS. Na 100 m je chyba rovinného přepočtu pod 1 cm.
/// </summary>
/// <param name="Name">„č.p. 15“, „č.e. 33“ nebo „bez čp/če“.</param>
/// <param name="CurrentUse">INSPIRE CurrentUseValue – IndividualResidence, Agriculture, Ancillary, PublicServices…</param>
/// <param name="AddressNames">Obec a část obce z adresy budovy.</param>
/// <param name="Rings">Vnější hranice půdorysů, uzavřené (první bod = poslední).</param>
public sealed record CadastreBuilding(
    string Id,
    string Name,
    string? CurrentUse,
    string? ParcelId,
    IReadOnlyList<string> AddressNames,
    IReadOnlyList<(double X, double Y)[]> Rings)
{
    public bool HasHouseNumber => Name.StartsWith("č.p.", StringComparison.OrdinalIgnoreCase)
                                  || Name.StartsWith("č.e.", StringComparison.OrdinalIgnoreCase);
}

public interface ICuzkBuildingService
{
    /// <summary>Půdorysy budov v okolí bodu (WGS84); null když služba neodpoví nebo vrátí chybu.</summary>
    Task<IReadOnlyList<CadastreBuilding>?> BuildingsAroundAsync(double latitude, double longitude, CancellationToken ct);
}

/// <summary>
/// Veřejná stahovací služba ČÚZK INSPIRE Buildings (WFS 2.0, bez poplatků a omezení přístupu, data
/// ČÚZK jsou od 2023 otevřená). Na rozdíl od obrázku katastrální mapy dává přesné půdorysy budov
/// s číslem popisným a využitím – společnou zeď dvou domů pak lze změřit, ne odhadovat z obrázku.
/// </summary>
public sealed class CuzkBuildingService(IHttpClientFactory httpClientFactory, ILogger<CuzkBuildingService> logger) : ICuzkBuildingService
{
    public const string BuildingsWfs = "https://services.cuzk.cz/wfs/inspire-bu-wfs.asp";

    /// <summary>Okruh dotazu. Vrací se celé budovy, které do čtverce zasahují – stačí na souseda i jeho dalšího souseda.</summary>
    public const double RadiusMeters = 40;

    public async Task<IReadOnlyList<CadastreBuilding>?> BuildingsAroundAsync(double latitude, double longitude, CancellationToken ct)
    {
        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");
            using var response = await http.GetAsync(WfsUrl(latitude, longitude, RadiusMeters), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ČÚZK WFS budov: HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            var buildings = ParseBuildings(await response.Content.ReadAsStringAsync(ct), latitude, longitude);
            if (buildings is null)
                logger.LogWarning("ČÚZK WFS budov: odpověď není FeatureCollection");
            return buildings;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or XmlException)
        {
            logger.LogWarning(ex, "ČÚZK WFS budov nedostupné");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>GetFeature bu:Building ve čtverci kolem bodu; souřadnice dotazu i odpovědi v EPSG:4326 (pořadí šířka, délka).</summary>
    public static string WfsUrl(double latitude, double longitude, double radiusMeters)
    {
        var (minLat, minLon, maxLat, maxLon) = CuzkMapService.BoundingBox(latitude, longitude, radiusMeters);
        return string.Create(CultureInfo.InvariantCulture,
            $"{BuildingsWfs}?service=WFS&version=2.0.0&request=GetFeature&typeNames=bu:Building&srsName=urn:ogc:def:crs:EPSG::4326&bbox={minLat:F6},{minLon:F6},{maxLat:F6},{maxLon:F6},urn:ogc:def:crs:EPSG::4326");
    }

    /// <summary>GML odpověď → budovy v místních metrech kolem (lat0, lon0). Chybová odpověď (ExceptionReport) = null.</summary>
    public static List<CadastreBuilding>? ParseBuildings(string gml, double lat0, double lon0)
    {
        var root = XDocument.Parse(gml).Root;
        if (root is null || root.Name.LocalName != "FeatureCollection")
            return null;

        XNamespace xlink = "http://www.w3.org/1999/xlink";
        var metersPerDegLon = 111_320.0 * Math.Cos(lat0 * Math.PI / 180);
        var buildings = new List<CadastreBuilding>();

        foreach (var building in root.Elements().Where(e => e.Name.LocalName == "member").Select(m => m.Elements().FirstOrDefault()))
        {
            if (building is null) continue;

            var rings = building.Descendants()
                .Where(e => e.Name.LocalName == "geometry2D")
                .SelectMany(g => g.Descendants().Where(e => e.Name.LocalName == "exterior"))
                .SelectMany(e => e.Descendants().Where(d => d.Name.LocalName == "posList"))
                .Select(p => ParseRing(p.Value, lat0, lon0, metersPerDegLon))
                .Where(r => r.Length >= 4)
                .ToList();
            if (rings.Count == 0) continue;

            var id = building.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";
            var name = building.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "text")?.Value.Trim() ?? "";
            var use = building.Descendants()
                .Where(e => e.Name.LocalName == "currentUse")
                .Select(e => e.Attribute(xlink + "title")?.Value)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var parcelHref = building.Descendants().FirstOrDefault(e => e.Name.LocalName == "cadastralParcel")?.Attribute(xlink + "href")?.Value;
            var parcel = parcelHref is null ? null : parcelHref[(parcelHref.LastIndexOf("Id=", StringComparison.Ordinal) + 3)..];
            var addressNames = building.Descendants()
                .Where(e => e.Name.LocalName == "AddressRepresentation")
                .SelectMany(a => a.Descendants().Where(e => e.Name.LocalName == "text"))
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();

            buildings.Add(new CadastreBuilding(id, name, use, parcel, addressNames, rings));
        }
        return buildings;
    }

    private static (double X, double Y)[] ParseRing(string posList, double lat0, double lon0, double metersPerDegLon)
    {
        var values = posList.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
            .ToArray();
        var points = new List<(double X, double Y)>(values.Length / 2 + 1);
        for (var i = 0; i + 1 < values.Length; i += 2)
            points.Add(((values[i + 1] - lon0) * metersPerDegLon, (values[i] - lat0) * 111_320.0));
        if (points.Count > 0 && points[0] != points[^1])
            points.Add(points[0]);
        return points.ToArray();
    }
}
