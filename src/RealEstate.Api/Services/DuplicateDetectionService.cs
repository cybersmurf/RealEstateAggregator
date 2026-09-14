using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Projekce inzerátu pro párování duplikátů – jen skalární pole, žádné navigace.
/// Public kvůli unit testům čisté párovací logiky.
/// </summary>
public sealed record DuplicateCandidate(
    Guid Id,
    Guid SourceId,
    PropertyType PropertyType,
    OfferType OfferType,
    decimal? Price,
    double? Latitude,
    double? Longitude,
    string? Municipality,
    double? AreaBuiltUp,
    double? AreaLand,
    DateTime FirstSeenAt,
    bool PreciseGps = true);

public sealed class DuplicateDetectionService(
    RealEstateDbContext ctx,
    ILogger<DuplicateDetectionService> logger) : IDuplicateDetectionService
{
    // ── Prahy párování ───────────────────────────────────────────────────────
    // Kalibrováno na reálné případy: stejný dům má napříč zdroji typicky cenu
    // na korunu stejnou a GPS pár desítek metrů od sebe (každý zdroj geokóduje jinak).
    // Radši duplikát přehlédnout než sloučit dva různé domy – proto přísné meze.

    /// <summary>Max. relativní rozdíl ceny (2 %) – pokrývá drobné rozdíly typu „vč./bez provize".</summary>
    private const double PriceTolerance = 0.02;

    /// <summary>Max. vzdálenost GPS bodů v metrech.</summary>
    private const double GpsMaxMeters = 300;

    /// <summary>
    /// Max. vzdálenost, když je aspoň jedna poloha jen geokódovaná Nominatimem ze středu obce/PSČ.
    /// Reálný případ: Bazoš "671 61 Znojmo" padl 2,3 km od domu v Práči.
    /// </summary>
    private const double ApproxGpsMaxMeters = 5_000;

    /// <summary>Fallback bez GPS: max. relativní rozdíl plochy (5 %).</summary>
    private const double AreaTolerance = 0.05;

    public async Task<DuplicateScanResultDto> DetectAsync(CancellationToken cancellationToken)
    {
        var candidates = await ctx.Listings
            .AsNoTracking()
            .Where(l => l.IsActive)
            .Select(l => new DuplicateCandidate(
                l.Id, l.SourceId, l.PropertyType, l.OfferType,
                l.Price, l.Latitude, l.Longitude,
                l.Municipality, l.AreaBuiltUp, l.AreaLand, l.FirstSeenAt,
                l.GeocodeSource != "nominatim"))
            .ToListAsync(cancellationToken);

        var mapping = BuildClusters(candidates); // dupId -> primaryId

        // Reset všech vazeb (i u neaktivních – primární inzerát mohl mezitím zmizet
        // a vazba na neaktivní primár by aktivní duplikát schovala z vyhledávání).
        await ctx.Listings
            .Where(l => l.DuplicateOfListingId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DuplicateOfListingId, (Guid?)null), cancellationToken);

        // Zápis po skupinách – jeden UPDATE na primární inzerát
        foreach (var group in mapping.GroupBy(kv => kv.Value))
        {
            var primaryId = group.Key;
            var dupIds = group.Select(kv => kv.Key).ToList();
            await ctx.Listings
                .Where(l => dupIds.Contains(l.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.DuplicateOfListingId, primaryId), cancellationToken);
        }

        var clusters = mapping.Values.Distinct().Count();
        logger.LogInformation(
            "Detekce duplikátů: {Active} aktivních inzerátů, {Clusters} skupin, {Dups} označených duplikátů",
            candidates.Count, clusters, mapping.Count);

        return new DuplicateScanResultDto(candidates.Count, clusters, mapping.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá párovací logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rozhodne, zda dva inzeráty popisují tutéž nemovitost.
    /// Nutné podmínky: jiný zdroj, stejný typ nemovitosti i nabídky, cena v toleranci 2 %.
    /// Plus jedna z evidencí: přesná GPS obou do 300 m, NEBO (bez přesné GPS) stejná cena
    /// na korunu + plochy bez rozporu + (GPS do 300 m, NEBO shodná plocha a GPS do 5 km či stejná obec).
    /// </summary>
    public static bool IsDuplicatePair(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.SourceId == b.SourceId) return false;              // duplicity v rámci zdroje řeší (source_id, external_id) unique
        if (a.PropertyType != b.PropertyType) return false;
        if (a.OfferType != b.OfferType) return false;

        if (a.Price is not > 0 || b.Price is not > 0) return false;
        var maxPrice = (double)Math.Max(a.Price.Value, b.Price.Value);
        var priceDiff = (double)Math.Abs(a.Price.Value - b.Price.Value);
        if (priceDiff > maxPrice * PriceTolerance) return false;

        double? distance =
            a.Latitude is not null && a.Longitude is not null &&
            b.Latitude is not null && b.Longitude is not null
                ? GpsDistanceMeters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value)
                : null;

        // Evidence 1: přesná GPS od zdroje u obou. Pak rozhoduje výhradně vzdálenost –
        // dva inzeráty 2 km od sebe nejsou tentýž dům, ani když sedí cena, obec i plocha.
        if (distance is not null && a.PreciseGps && b.PreciseGps)
            return distance <= GpsMaxMeters;

        // Evidence 2 (GPS chybí, nebo je jen geokódovaná z obce/PSČ): cena na korunu stejná
        // a plochy si neodporují. Přísnější než GPS větev, protože „7 490 000 Kč ve Znojmě"
        // můžou být dva různé domy.
        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        if (priceDiff != 0 || builtUp == false || land == false)
            return false;

        // Do 300 m stačí cena na korunu – řada zdrojů plochy vůbec nemá (PRODEJMETO, MMR…)
        if (distance <= GpsMaxMeters)
            return true;

        // Dál už je potřeba i shodná plocha, plus blízkost nebo stejná obec
        if (builtUp != true && land != true)
            return false;

        if (distance <= ApproxGpsMaxMeters)
            return true;

        return !string.IsNullOrWhiteSpace(a.Municipality)
            && string.Equals(a.Municipality.Trim(), b.Municipality?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sestaví skupiny duplikátů (union-find) a vrátí mapu duplikát → primární inzerát.
    /// Primární = nejstarší <c>FirstSeenAt</c> ve skupině (má nejdelší cenovou historii);
    /// remíza se rozhoduje podle Id, aby byl výsledek deterministický.
    /// </summary>
    public static Dictionary<Guid, Guid> BuildClusters(IReadOnlyList<DuplicateCandidate> candidates)
    {
        var parent = new Dictionary<Guid, Guid>();

        Guid Find(Guid x)
        {
            while (parent.TryGetValue(x, out var p) && p != x)
            {
                parent[x] = parent.TryGetValue(p, out var gp) ? gp : p; // path halving
                x = parent[x];
            }
            return x;
        }

        void Union(Guid x, Guid y)
        {
            var (rx, ry) = (Find(x), Find(y));
            if (rx != ry) { parent.TryAdd(rx, rx); parent.TryAdd(ry, ry); parent[ry] = rx; }
        }

        // Kandidáty porovnáváme jen uvnitř (typ, nabídka) skupiny a jen v cenovém okně ±2 %
        // – z O(n²) přes všechno je O(n²) přes pár desítek inzerátů se stejnou cenou.
        var pairs = new List<(DuplicateCandidate A, DuplicateCandidate B)>();
        foreach (var group in candidates.Where(c => c.Price is > 0).GroupBy(c => (c.PropertyType, c.OfferType)))
        {
            var sorted = group.OrderBy(c => c.Price).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                var a = sorted[i];
                var maxPrice = (double)a.Price!.Value * (1 + PriceTolerance);
                for (var j = i + 1; j < sorted.Count && (double)sorted[j].Price!.Value <= maxPrice; j++)
                {
                    if (IsDuplicatePair(a, sorted[j]))
                        pairs.Add((a, sorted[j]));
                }
            }
        }

        // Pár platí, jen když si oba inzeráty v cizím zdroji odpovídají jednoznačně.
        // Parcelace v Božicích (pozemky č. 1–8, stejná cena i výměra) na Bazoši i SREALITY
        // se jinak slila do jedné skupiny a 12 skutečných pozemků zmizelo z výsledků.
        var matchesInSource = pairs
            .SelectMany(p => new[] { (p.A.Id, p.B.SourceId), (p.B.Id, p.A.SourceId) })
            .CountBy(k => k)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (a, b) in pairs)
        {
            if (matchesInSource[(a.Id, b.SourceId)] == 1 && matchesInSource[(b.Id, a.SourceId)] == 1)
                Union(a.Id, b.Id);
        }

        // Skupiny → primární podle FirstSeenAt
        var byId = candidates.ToDictionary(c => c.Id);
        var clusters = parent.Keys.GroupBy(Find);
        var result = new Dictionary<Guid, Guid>();

        foreach (var cluster in clusters)
        {
            var members = cluster.Select(id => byId[id]).ToList();
            if (members.Count < 2) continue;

            // Dva inzeráty ze stejného zdroje ve skupině = řetěz přes různé nemovitosti; radši nic
            if (members.DistinctBy(m => m.SourceId).Count() < members.Count) continue;

            var primary = members.OrderBy(m => m.FirstSeenAt).ThenBy(m => m.Id).First();
            foreach (var m in members.Where(m => m.Id != primary.Id))
                result[m.Id] = primary.Id;
        }

        return result;
    }

    /// <summary>Přibližná vzdálenost dvou GPS bodů v metrech (equirektangulární aproximace – na stovky metrů přesná dost).</summary>
    public static double GpsDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double MetersPerDegreeLat = 111_320;
        var meanLatRad = (lat1 + lat2) / 2 * Math.PI / 180;
        var dy = (lat2 - lat1) * MetersPerDegreeLat;
        var dx = (lon2 - lon1) * MetersPerDegreeLat * Math.Cos(meanLatRad);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>true = shoda v toleranci 5 %, false = rozpor, null = u jednoho chybí.</summary>
    private static bool? CompareAreas(double? a, double? b)
        => a is > 0 && b is > 0
            ? Math.Abs(a.Value - b.Value) <= Math.Max(a.Value, b.Value) * AreaTolerance
            : null;
}
