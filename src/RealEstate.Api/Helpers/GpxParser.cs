using System.Xml.Linq;

namespace RealEstate.Api.Helpers;

/// <summary>
/// Jednoduchý GPX parser – extrahuje waypoints ze souboru GPX.
/// Podporuje track points (&lt;trkpt&gt;), route points (&lt;rtept&gt;) a standalone waypoints (&lt;wpt&gt;).
/// </summary>
public static class GpxParser
{
    // GPX 1.0 i 1.1 namespace
    private static readonly XNamespace Gpx10 = "http://www.topografix.com/GPX/1/0";
    private static readonly XNamespace Gpx11 = "http://www.topografix.com/GPX/1/1";

    /// <summary>
    /// Parsuje GPX stream a vrátí seřazené souřadnice (lat, lon) jako WKT LINESTRING.
    /// Pořadí priorit: trkpt → rtept → wpt.
    /// </summary>
    /// <exception cref="InvalidDataException">Pokud GPX neobsahuje žádné souřadnice.</exception>
    public static GpxParseResult Parse(Stream stream)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(stream);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Soubor nelze přečíst jako XML: {ex.Message}", ex);
        }

        var root = doc.Root
            ?? throw new InvalidDataException("GPX soubor je prázdný.");

        // Detekuj namespace
        var ns = root.Name.Namespace;
        if (ns != Gpx10 && ns != Gpx11 && ns != XNamespace.None)
        {
            // Neznámý namespace – zkusíme bez namespace jako fallback
        }

        // 1. Track points
        var points = root.Descendants(ns + "trkpt")
            .Select(el => ParsePoint(el))
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        // 2. Route points (fallback)
        if (points.Count < 2)
        {
            points = root.Descendants(ns + "rtept")
                .Select(el => ParsePoint(el))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
        }

        // 3. Standalone waypoints (fallback)
        if (points.Count < 2)
        {
            points = root.Descendants(ns + "wpt")
                .Select(el => ParsePoint(el))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
        }

        if (points.Count < 2)
            throw new InvalidDataException(
                "GPX soubor neobsahuje žádné track/route/waypoints (potřebujeme alespoň 2 body).");

        // Sestaví WKT LINESTRING: PostGIS souřadnice jsou (lon lat)
        var wkt = "LINESTRING(" +
                  string.Join(", ", points.Select(p => FormattableString.Invariant($"{p.Lon} {p.Lat}"))) +
                  ")";

        return new GpxParseResult(
            LineStringWkt: wkt,
            PointCount: points.Count,
            StartLat: points[0].Lat,
            StartLon: points[0].Lon,
            EndLat: points[^1].Lat,
            EndLon: points[^1].Lon
        );
    }

    private static (double Lat, double Lon)? ParsePoint(XElement el)
    {
        var latStr = el.Attribute("lat")?.Value;
        var lonStr = el.Attribute("lon")?.Value;

        if (string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lonStr))
            return null;

        if (!double.TryParse(latStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lat))
            return null;

        if (!double.TryParse(lonStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lon))
            return null;

        // Základní validace – ČR je přibližně lat 48.5–51.1, lon 12.1–18.9
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            return null;

        return (lat, lon);
    }
}

/// <summary>Výsledek parsování GPX souboru.</summary>
public record GpxParseResult(
    string LineStringWkt,
    int PointCount,
    double StartLat,
    double StartLon,
    double EndLat,
    double EndLon
);
