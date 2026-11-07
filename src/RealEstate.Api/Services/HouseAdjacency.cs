using System.Globalization;
using RealEstate.Api.Services.Duplicates;
using RealEstate.Api.Services.Vision;

namespace RealEstate.Api.Services;

/// <summary>Sousední budova, se kterou má dům společnou zeď.</summary>
public sealed record AttachedBuilding(string Name, string? CurrentUse, double WallMeters);

/// <summary>
/// Poloha domu podle půdorysů v katastru. <see cref="Position"/> null = z katastru nejde určit
/// (GPS neleží na obytné budově, dům v katastru ještě není…), <see cref="Reason"/> pak říká proč.
/// </summary>
public sealed record CadastreVerdict(
    string? Position,
    string Reason,
    string? BuildingName = null,
    IReadOnlyList<AttachedBuilding>? Houses = null,
    IReadOnlyList<AttachedBuilding>? Outbuildings = null)
{
    public bool IsConclusive => Position is not null;

    /// <summary>Fakta z katastru pro obrazový model – anglicky jako zbytek dotazu.</summary>
    public string PromptFacts()
    {
        if (!IsConclusive)
            return "Cadastre (ČÚZK building register): there is no residential building exactly at the crosshair, so the maps may not show the house for sale – rely mainly on the listing photos.";

        static string List(IReadOnlyList<AttachedBuilding>? items)
            => items is { Count: > 0 }
                ? string.Join(", ", items.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Name} ({b.WallMeters:F0} m of shared wall)")))
                : "none";

        return $"Cadastre (ČÚZK building register, exact footprints): the building under the crosshair is {BuildingName}. "
               + $"Neighbouring main buildings sharing a wall with it: {List(Houses)}. "
               + $"Outbuildings without a house number touching it: {List(Outbuildings)}. "
               + "The register can miss unregistered annexes, and the crosshair can sit on a wrong building – check both against the photos.";
    }
}

/// <summary>
/// Deterministická poloha domu z půdorysů budov v katastru (ČÚZK INSPIRE Buildings). Obrazový model
/// četl společnou zeď dvou půdorysů na obrázku mapy jako mezeru („na všech stranách je mezera…“) –
/// 11 ze 45 domů zkontrolovaných 7. 11. 2026 mělo polohu špatně. Tady se délka společné hranice měří.
/// Kontrola na 120 náhodných domech ze Sreality s vlastní GPS (7. 11. 2026): 32 GPS neleží na obytné
/// budově (střed obce, ulice); z 88 zbylých katastr souhlasil s makléřem (samostatný × přistavěný)
/// v 66. Rozpory byly špatně umístěný špendlík (Černín), nezapsané přístavby (Hostim) i nepřesný údaj
/// makléře (Horní Věstonice, Damnice) – proto katastr rozhoduje jen ve shodě s dalším zdrojem
/// (<see cref="HousePositionService.Reconcile"/>).
/// </summary>
public static class HouseAdjacency
{
    /// <summary>Hranice sousedních budov se v katastru kryjí na decimetry. 1 m už spojí domy oddělené okapovou uličkou (Jiřice u Mor. Budějovic).</summary>
    public const double WallTolerance = 0.5;

    /// <summary>Kratší dotyk je roh nebo zídka, ne společná zeď.</summary>
    public const double MinWall = 2.0;

    /// <summary>GPS mimo půdorys (chodník před domem, dvůr) se přiřadí nejbližší obytné budově do této vzdálenosti.</summary>
    public const double MaxPinDistance = 5.0;

    private const double Step = 0.25;

    /// <summary>Využití, které u domu na prodej nečekáme – GPS na kostele nebo obecním úřadě je střed obce.</summary>
    private static readonly string[] NonDwellingUses = ["PublicServices", "Industrial", "Office", "Trade", "CommerceAndServices"];

    private static readonly string[] ResidentialUses =
        ["IndividualResidence", "Residential", "CollectiveResidence", "TwoDwellings", "MoreThanTwoDwellings", "ResidenceForCommunities"];

    /// <summary>Hlavní budova: s číslem popisným/evidenčním, nebo obytná bez čísla (novostavba před přidělením č.p.). Ostatní jsou vedlejší stavby.</summary>
    public static bool IsMainBuilding(CadastreBuilding b)
        => b.HasHouseNumber || (b.CurrentUse is not null && ResidentialUses.Contains(b.CurrentUse, StringComparer.OrdinalIgnoreCase));

    private static bool IsDwellingCandidate(CadastreBuilding b)
        => IsMainBuilding(b) && (b.CurrentUse is null || !NonDwellingUses.Contains(b.CurrentUse, StringComparer.OrdinalIgnoreCase));

    /// <summary>Rozhodne z půdorysů kolem GPS inzerátu (počátek souřadnic) – viz <see cref="CadastreVerdict"/>.</summary>
    public static CadastreVerdict Analyze(IReadOnlyList<CadastreBuilding> buildings, string? municipality)
    {
        if (buildings.Count == 0)
            return new CadastreVerdict(null, "Katastr: u GPS inzerátu není žádná budova (novostavba ještě nezapsaná?).");

        var target = buildings.FirstOrDefault(b => IsDwellingCandidate(b) && Contains(b, 0, 0));
        if (target is null)
        {
            if (buildings.FirstOrDefault(b => IsMainBuilding(b) && Contains(b, 0, 0)) is { } other)
                return new CadastreVerdict(null, $"Katastr: GPS inzerátu leží na budově {other.Name} s jiným využitím ({other.CurrentUse}) – nejspíš střed obce, ne dům.");

            var ranked = buildings.Where(IsDwellingCandidate)
                .Select(b => (Building: b, Distance: DistanceFromOrigin(b)))
                .OrderBy(x => x.Distance)
                .ToList();
            if (ranked.Count == 0 || ranked[0].Distance > MaxPinDistance)
                return new CadastreVerdict(null, ranked.Count == 0
                    ? "Katastr: v okolí GPS inzerátu není obytná budova."
                    : string.Create(CultureInfo.InvariantCulture, $"Katastr: GPS inzerátu neleží na obytné budově (nejbližší je {ranked[0].Distance:F0} m daleko) – nejspíš střed obce nebo ulice."));

            target = ranked[0].Building;
            var limit = ranked[0].Distance + 2;
            // GPS mezi dvěma domy, které spolu nesousedí – nevíme, který z nich se prodává
            var targetParts = PartsOf(buildings, target);
            var rival = buildings.FirstOrDefault(b => !targetParts.Contains(b) && IsMainBuilding(b)
                                                      && DistanceFromOrigin(b) <= limit && SharedWall(target, b) < MinWall);
            if (rival is not null)
                return new CadastreVerdict(null, $"Katastr: GPS inzerátu leží mezi budovami {target.Name} a {rival.Name} – nejde říct, která se prodává.");
        }

        if (!MunicipalityMatches(municipality, target.AddressNames))
            return new CadastreVerdict(null, $"Katastr: budova u GPS ({target.Name}) patří k obci {string.Join(" / ", target.AddressNames)}, inzerát uvádí {municipality} – GPS míří jinam.");

        // Jedno č.p. bývá v katastru rozdělené na víc půdorysů – sousedem není a u souseda se počítá jednou
        var parts = PartsOf(buildings, target);
        var attached = buildings
            .Where(b => !parts.Contains(b))
            .Select(b => (Building: b, Wall: parts.Sum(p => SharedWall(p, b))))
            .Where(x => x.Wall >= MinWall)
            .GroupBy(x => x.Building.HasHouseNumber ? x.Building.Name : x.Building.Id)
            .Select(g => (Building: g.First().Building, Wall: Math.Round(g.Sum(x => x.Wall), 1)))
            .ToList();
        var houses = attached.Where(x => IsMainBuilding(x.Building)).ToList();
        var outbuildings = attached.Where(x => !IsMainBuilding(x.Building))
            .Select(x => new AttachedBuilding(x.Building.Name, x.Building.CurrentUse, x.Wall)).ToList();
        var houseWalls = houses.Select(x => new AttachedBuilding(x.Building.Name, x.Building.CurrentUse, x.Wall)).ToList();

        string position;
        string reason;
        if (houses.Count == 0)
        {
            position = HousePositions.Detached;
            reason = $"Katastr: budova {target.Name} nemá společnou zeď s žádnou sousední hlavní budovou.";
            if (outbuildings.Count > 0)
                reason += string.Create(CultureInfo.InvariantCulture,
                    $" Přiléhá k ní jen vedlejší stavba bez č.p. (společná zeď {string.Join(" a ", outbuildings.Select(o => $"{o.WallMeters:F0} m"))}) – za přisazení se nepočítá.");
        }
        else if (houses.Count >= 2)
        {
            position = HousePositions.Terraced;
            reason = $"Katastr: budova {target.Name} má společné zdi s {Walls(houseWalls)} – řadová zástavba.";
        }
        else
        {
            // Jeden soused: když na něj navazují další domy, je to konec řady, jinak dvojdům
            var neighbourParts = PartsOf(buildings, houses[0].Building);
            var continues = buildings.Any(b => !parts.Contains(b) && !neighbourParts.Contains(b) && IsMainBuilding(b)
                                              && neighbourParts.Any(n => SharedWall(n, b) >= MinWall));
            position = continues ? HousePositions.Corner : HousePositions.SemiDetached;
            reason = continues
                ? $"Katastr: budova {target.Name} má společnou zeď jen s {Walls(houseWalls)}, na který navazují další domy – krajní dům řady."
                : $"Katastr: budova {target.Name} má společnou zeď s {Walls(houseWalls)}, ten je z druhé strany volný – dvojdům.";
        }

        return new CadastreVerdict(position, reason, target.Name, houseWalls, outbuildings);
    }

    /// <summary>Půdorysy téže budovy: ona sama a další se stejným číslem popisným/evidenčním.</summary>
    private static List<CadastreBuilding> PartsOf(IReadOnlyList<CadastreBuilding> buildings, CadastreBuilding building)
        => buildings.Where(b => ReferenceEquals(b, building) || (building.HasHouseNumber && b.HasHouseNumber && b.Name == building.Name)).ToList();

    private static string Walls(IEnumerable<AttachedBuilding> items)
        => string.Join(" a ", items.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Name} ({b.WallMeters:F0} m)")));

    /// <summary>
    /// Obec z adresy budovy proti obci inzerátu, bez diakritiky a velikosti písmen. Stačí, když jedna
    /// obsahuje druhou („Znojmo“ × „Znojmo-Přímětice“); bez údajů se nekontroluje.
    /// </summary>
    public static bool MunicipalityMatches(string? municipality, IReadOnlyList<string> addressNames)
    {
        if (string.IsNullOrWhiteSpace(municipality) || addressNames.Count == 0)
            return true;
        var listing = DuplicateDetectionService.NormalizeText(municipality);
        return addressNames.Select(DuplicateDetectionService.NormalizeText)
            .Any(n => n.Length >= 3 && (listing.Contains(n, StringComparison.Ordinal) || n.Contains(listing, StringComparison.Ordinal)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Geometrie v rovině (metry)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Délka hranice <paramref name="a"/>, která leží do <see cref="WallTolerance"/> od souběžné hranice <paramref name="b"/>
    /// – společná zeď. Vzorkuje se po 25 cm, takže nezáleží na tom, jestli mají obě budovy vrcholy na stejných místech;
    /// kolmé hrany u konce zdi (a dotyk „T") se nepočítají.
    /// </summary>
    public static double SharedWall(CadastreBuilding a, CadastreBuilding b)
    {
        if (!BoundsTouch(a, b)) return 0;
        var total = 0.0;
        foreach (var ring in a.Rings)
        {
            for (var i = 0; i + 1 < ring.Length; i++)
            {
                var (x1, y1) = ring[i];
                var (x2, y2) = ring[i + 1];
                var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                if (length == 0) continue;
                var samples = Math.Max(1, (int)Math.Ceiling(length / Step));
                for (var k = 0; k < samples; k++)
                {
                    var t = (k + 0.5) / samples;
                    if (NearParallelBoundary(b, x1 + t * (x2 - x1), y1 + t * (y2 - y1), (x2 - x1) / length, (y2 - y1) / length))
                        total += length / samples;
                }
            }
        }
        return total;
    }

    /// <summary>Úhel, do kterého se dvě hrany ještě berou jako souběžné (sin 25°).</summary>
    private const double MaxSinAngle = 0.42;

    /// <summary>
    /// Bod leží podél souběžné hrany <paramref name="b"/> (kolmý průmět padne dovnitř hrany) a nejvýš
    /// <see cref="WallTolerance"/> od ní. Průčelí dvou domů v jedné uliční čáře na sebe navazují, ale zeď to není.
    /// </summary>
    private static bool NearParallelBoundary(CadastreBuilding b, double px, double py, double ux, double uy)
    {
        foreach (var ring in b.Rings)
        {
            for (var i = 0; i + 1 < ring.Length; i++)
            {
                var (x1, y1) = ring[i];
                var (x2, y2) = ring[i + 1];
                var (dx, dy) = (x2 - x1, y2 - y1);
                var l2 = dx * dx + dy * dy;
                if (l2 == 0 || Math.Abs(ux * dy - uy * dx) / Math.Sqrt(l2) > MaxSinAngle) continue;
                var t = ((px - x1) * dx + (py - y1) * dy) / l2;
                if (t is < 0 or > 1) continue;
                if (SegmentDistance(px, py, x1, y1, x2, y2) <= WallTolerance) return true;
            }
        }
        return false;
    }

    private static double SegmentDistance(double px, double py, double x1, double y1, double x2, double y2)
    {
        var (dx, dy) = (x2 - x1, y2 - y1);
        var l2 = dx * dx + dy * dy;
        var t = l2 == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / l2, 0, 1);
        var (ex, ey) = (px - x1 - t * dx, py - y1 - t * dy);
        return Math.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>Vzdálenost GPS (počátku) od budovy; 0 uvnitř půdorysu.</summary>
    public static double DistanceFromOrigin(CadastreBuilding b)
        => Contains(b, 0, 0) ? 0 : DistanceToBoundary(b, 0, 0);

    /// <summary>Bod uvnitř vnější hranice – i ve dvoře uzavřeném domem (vnitřní hranice se ignorují).</summary>
    public static bool Contains(CadastreBuilding b, double x, double y)
    {
        foreach (var ring in b.Rings)
        {
            var inside = false;
            for (var i = 0; i + 1 < ring.Length; i++)
            {
                var (x1, y1) = ring[i];
                var (x2, y2) = ring[i + 1];
                if ((y1 > y) != (y2 > y) && x < (x2 - x1) * (y - y1) / (y2 - y1) + x1)
                    inside = !inside;
            }
            if (inside) return true;
        }
        return false;
    }

    private static double DistanceToBoundary(CadastreBuilding b, double px, double py)
    {
        var best = double.MaxValue;
        foreach (var ring in b.Rings)
            for (var i = 0; i + 1 < ring.Length; i++)
                best = Math.Min(best, SegmentDistance(px, py, ring[i].X, ring[i].Y, ring[i + 1].X, ring[i + 1].Y));
        return best;
    }

    private static bool BoundsTouch(CadastreBuilding a, CadastreBuilding b)
    {
        var (aMinX, aMinY, aMaxX, aMaxY) = Bounds(a);
        var (bMinX, bMinY, bMaxX, bMaxY) = Bounds(b);
        return aMinX <= bMaxX + WallTolerance && bMinX <= aMaxX + WallTolerance
               && aMinY <= bMaxY + WallTolerance && bMinY <= aMaxY + WallTolerance;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(CadastreBuilding b)
    {
        var points = b.Rings.SelectMany(r => r).ToList();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
}
