using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
    bool PreciseGps = true,
    string? District = null,
    string? Title = null,
    string? Disposition = null,
    string? Description = null,
    bool IsActive = true);

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

    /// <summary>
    /// Širší tolerance ceny pro kopii se zpožděnou cenou: realitka zlevní na Sreality, ale na iDNES
    /// nechá starou cenu (Znojmo centrum: 7,43 vs. 6,90 mil., 7 %). Platí jen s přísnou shodou ploch.
    /// </summary>
    private const double LaggingPriceTolerance = 0.10;

    /// <summary>Max. vzdálenost GPS bodů v metrech.</summary>
    private const double GpsMaxMeters = 300;

    /// <summary>
    /// Max. vzdálenost, když je aspoň jedna poloha jen geokódovaná Nominatimem ze středu obce/PSČ.
    /// Reálný případ: Bazoš "671 61 Znojmo" padl 2,3 km od domu v Práči.
    /// </summary>
    private const double ApproxGpsMaxMeters = 5_000;

    /// <summary>Fallback bez GPS: max. relativní rozdíl plochy (5 %).</summary>
    private const double AreaTolerance = 0.05;

    /// <summary>Opakovaný inzerát téhož domu v jednom zdroji: GPS musí být prakticky totožná.</summary>
    private const double RepeatGpsMaxMeters = 100;

    /// <summary>
    /// Nad tuhle vzdálenost dvě „přesné" GPS vylučují shodu. Mezi 300 m a touto mezí rozhodují
    /// další důkazy – špendlík realitky bývá o stovky metrů vedle (Nemovitosti Znojmo, Jevišovice: 480 m).
    /// </summary>
    private const double PreciseGpsRejectMeters = 1_500;

    /// <summary>Shodná dispozice + cena na korunu stačí jen do této vzdálenosti.</summary>
    private const double DispositionGpsMaxMeters = 1_000;

    /// <summary>Pod touto cenou se „cena na korunu" opakuje příliš často (nájmy, garáže, zahrádky).</summary>
    private const decimal ExactPriceEvidenceMin = 500_000m;

    /// <summary>Titulek kratší než tohle je moc obecný na to, aby byl důkazem („Prodej domu Znojmo").</summary>
    private const int TitleEvidenceMinLength = 30;

    /// <summary>
    /// Stejný popis = aspoň 90 % pětic slov kratšího textu najdeme i v delším. Měřeno 5. 10. 2026 na
    /// dvojicích se stejnou cenou: pod 0,9 začínají různé byty jednoho developera se společnou šablonou.
    /// </summary>
    private const double DescriptionMatchMin = 0.9;

    private const int DescriptionShingleWords = 5;

    /// <summary>Kratší popis („Prodej bytu, volejte") nic nedokazuje.</summary>
    private const int DescriptionMinShingles = 40;

    private static readonly ConditionalWeakTable<string, HashSet<int>> DescriptionShingleCache = new();

    /// <summary>
    /// Zdroje, které posílají GPS, ale je to jen střed obce geokódovaný portálem
    /// (medián odchylky proti Sreality 0,8–3 km, měřeno 30. 9. 2026). Bereme je jako přibližné.
    /// </summary>
    public static readonly string[] ApproxGpsSources = ["REALITYCECHY", "REALITYMIX", "REALCITY", "BEZREALITKY", "ULOVDOMOV"];

    /// <summary>
    /// Typy, které různé zdroje zaměňují u téže nemovitosti: chata/dům, dům/ostatní (Reas),
    /// komerční/dům (penziony, apartmány), garáž/ostatní (iDNES).
    /// </summary>
    private static readonly HashSet<(PropertyType, PropertyType)> CompatibleTypes =
    [
        (PropertyType.House, PropertyType.Cottage), (PropertyType.Cottage, PropertyType.House),
        (PropertyType.House, PropertyType.Other), (PropertyType.Other, PropertyType.House),
        (PropertyType.House, PropertyType.Commercial), (PropertyType.Commercial, PropertyType.House),
        (PropertyType.Cottage, PropertyType.Other), (PropertyType.Other, PropertyType.Cottage),
        (PropertyType.Garage, PropertyType.Other), (PropertyType.Other, PropertyType.Garage),
    ];

    public async Task<DuplicateScanResultDto> DetectAsync(CancellationToken cancellationToken)
    {
        // Aktivní inzeráty + stažené, ke kterým má někdo vlastní záznam (stav, poznámky, fotky
        // z prohlídky). Ty se nepárují mezi sebou, jen se připojí ke své živé kopii – dům, kde
        // už uživatel byl, se jinak po návratu na jiný portál tváří jako neviděný.
        var all = await ctx.Listings
            .AsNoTracking()
            .Where(l => l.IsActive
                        || l.UserStates.Any(s => s.Status != "New" || (s.Notes != null && s.Notes != ""))
                        || ctx.UserListingPhotos.Any(p => p.ListingId == l.Id))
            .Select(l => new DuplicateCandidate(
                l.Id, l.SourceId, l.PropertyType, l.OfferType,
                l.Price, l.Latitude, l.Longitude,
                l.Municipality, l.AreaBuiltUp, l.AreaLand, l.FirstSeenAt,
                l.GeocodeSource != "nominatim" && !ApproxGpsSources.Contains(l.SourceCode),
                l.District, l.Title, l.Disposition,
                l.Price >= ExactPriceEvidenceMin ? l.Description : null,
                l.IsActive))
            .ToListAsync(cancellationToken);

        var candidates = all.Where(c => c.IsActive).ToList();
        var mapping = BuildClusters(candidates); // dupId -> primaryId

        var remembered = AttachRemembered(candidates, all.Where(c => !c.IsActive).ToList(), mapping);
        foreach (var (rememberedId, primaryId) in remembered)
            mapping[rememberedId] = primaryId;

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
            "Detekce duplikátů: {Active} aktivních inzerátů, {Clusters} skupin, {Dups} označených duplikátů, {Remembered} stažených se záznamem připojeno k živé kopii",
            candidates.Count, clusters, mapping.Count, remembered.Count);

        return new DuplicateScanResultDto(candidates.Count, clusters, mapping.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá párovací logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rozhodne, zda dva inzeráty popisují tutéž nemovitost.
    /// Nutné podmínky: jiný zdroj, stejný typ nemovitosti i nabídky, cena v toleranci 2 %.
    /// Plus jedna z evidencí: stejná cena na korunu a stejný dlouhý titulek nebo popis, NEBO
    /// přesná GPS obou do 300 m, NEBO (bez přesné GPS) stejná cena
    /// na korunu + plochy bez rozporu + (GPS do 300 m, NEBO shodná plocha a GPS do 5 km či stejná obec).
    /// </summary>
    public static bool IsDuplicatePair(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.SourceId == b.SourceId) return false;              // opakování v jednom zdroji řeší IsSameSourceRepeat
        if (!TypesCompatible(a.PropertyType, b.PropertyType)) return false;
        if (a.OfferType != b.OfferType) return false;

        if (a.Price is not > 0 || b.Price is not > 0) return false;
        var maxPrice = (double)Math.Max(a.Price.Value, b.Price.Value);
        var priceDiff = (double)Math.Abs(a.Price.Value - b.Price.Value);

        double? distance =
            a.Latitude is not null && a.Longitude is not null &&
            b.Latitude is not null && b.Longitude is not null
                ? GpsDistanceMeters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value)
                : null;

        if (priceDiff > maxPrice * PriceTolerance)
            return priceDiff <= maxPrice * LaggingPriceTolerance && IsLaggingPriceCopy(a, b, distance);

        // Dvě přesné GPS dál než 1,5 km shodu vylučují bez ohledu na titulek, cenu i plochy
        if (distance > PreciseGpsRejectMeters && a.PreciseGps && b.PreciseGps)
            return false;

        // Evidence 0: stejný dlouhý titulek a cena na korunu – realitka exportuje tentýž inzerát
        // na svůj web i na Bazoš („Prodej dvougeneračního rodinného domu s výhledem na…").
        // Platí i když Bazoš vytáhl z popisu jinou výměru; okresy se nesmí lišit.
        if (priceDiff == 0 && a.Price >= ExactPriceEvidenceMin && TitlesMatch(a.Title, b.Title)
            && !DistrictsDiffer(a.District, b.District))
            return true;

        // Evidence 0b: stejný popis a cena na korunu – realitka vloží tentýž text na Sreality i Bazoš
        // pod jiným titulkem a Bazoš z něj vytáhne jiné výměry (Prosiměřice: 237/929 vs. 207/722 m²).
        if (priceDiff == 0 && a.Price >= ExactPriceEvidenceMin && !DistrictsDiffer(a.District, b.District)
            && DescriptionsMatch(a.Description, b.Description))
            return true;

        // Evidence 1: přesná GPS od zdroje u obou. Do 300 m je to tentýž dům, nad 1,5 km určitě ne
        // (dva inzeráty 2 km od sebe nejsou tentýž dům, ani když sedí cena, obec i plocha).
        // Mezi tím rozhodují přísnější důkazy níž – špendlík realitky bývá o stovky metrů vedle.
        if (distance is not null && a.PreciseGps && b.PreciseGps)
        {
            if (distance <= GpsMaxMeters) return true;
        }

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

        // Bez ploch (Nemovitosti Znojmo je neuvádí): prodej za stejnou cenu na korunu, stejná
        // dispozice a do 1 km – „3+kk za 7 900 000 Kč v Jevišovicích" dvakrát není náhoda.
        if (a.OfferType == OfferType.Sale && a.Price >= ExactPriceEvidenceMin
            && distance <= DispositionGpsMaxMeters && DispositionsMatch(a.Disposition, b.Disposition))
            return true;

        // Dál už je potřeba i shodná plocha, plus blízkost nebo stejná obec
        if (builtUp != true && land != true)
            return false;

        if (distance <= ApproxGpsMaxMeters)
            return true;

        if (MunicipalityMatches(a.Municipality, b.Municipality))
            return true;

        // Obec chybí (iDNES, Bazoš, REMAX, Reas… ji neplní): stejný okres + užitná plocha
        // i pozemek na metr stejné je dost silná shoda i bez obce a GPS.
        var municipalityMissing = string.IsNullOrWhiteSpace(a.Municipality) || string.IsNullOrWhiteSpace(b.Municipality);
        return municipalityMissing
            && builtUp == true && land == true
            && DistrictMatches(a.District, b.District);
    }

    /// <summary>
    /// Kopie téhož domu, kde jeden portál ještě drží starou cenu. Bez shody ceny musí sedět všechno
    /// ostatní: prodej, užitná plocha i pozemek na metr, dispozice, okres a poloha (do 5 km nebo obec).
    /// </summary>
    private static bool IsLaggingPriceCopy(DuplicateCandidate a, DuplicateCandidate b, double? distance)
    {
        if (a.OfferType != OfferType.Sale || a.Price < ExactPriceEvidenceMin || b.Price < ExactPriceEvidenceMin) return false;
        if (a.AreaBuiltUp is not > 0 || b.AreaBuiltUp is not > 0 || a.AreaLand is not > 0 || b.AreaLand is not > 0) return false;
        if (Math.Abs(a.AreaBuiltUp.Value - b.AreaBuiltUp.Value) > 0.5 || Math.Abs(a.AreaLand.Value - b.AreaLand.Value) > 0.5) return false;
        if (!DispositionsMatch(a.Disposition, b.Disposition)) return false;
        if (!DistrictMatches(a.District, b.District)) return false;
        if (distance is not null)
            return distance <= ApproxGpsMaxMeters && !(a.PreciseGps && b.PreciseGps && distance > PreciseGpsRejectMeters);
        return MunicipalityMatches(a.Municipality, b.Municipality);
    }

    /// <summary>
    /// Tentýž inzerát podaný v jednom zdroji vícekrát (SREALITY dům Kuchařovická 4×).
    /// Přísnější než cross-source pár: cena na korunu, plochy stejné (obě známé), a buď přesná
    /// GPS do 100 m, nebo bez přesné GPS stejná obec a známý stejný pozemek.
    /// </summary>
    public static bool IsSameSourceRepeat(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.SourceId != b.SourceId) return false;
        // Tentýž dům bývá v jednom zdroji veden jako „rodinný dům" i „chalupa" (Reality Čechy, REALmix)
        if (!TypesCompatible(a.PropertyType, b.PropertyType) || a.OfferType != b.OfferType) return false;
        // Pozemky (parcelace: stejná cena, výměra i poloha) a byty (developer: shodné jednotky
        // v jednom domě) se legitimně opakují – opakování řešíme jen u budov.
        if (a.PropertyType is PropertyType.Land or PropertyType.Apartment
            || b.PropertyType is PropertyType.Land or PropertyType.Apartment) return false;
        if (a.Price is not > 0 || b.Price is not > 0 || a.Price != b.Price) return false;

        // Znovu vložený inzerát (Bazoš): stejný dlouhý titulek a cena, i když parser vytáhl jinou výměru
        if (a.Price >= ExactPriceEvidenceMin && TitlesMatch(a.Title, b.Title))
            return true;

        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        if (builtUp == false || land == false) return false;
        if (builtUp != true && land != true) return false;

        var bothPreciseGps = a.PreciseGps && b.PreciseGps
            && a.Latitude is not null && a.Longitude is not null
            && b.Latitude is not null && b.Longitude is not null;
        if (bothPreciseGps)
            return GpsDistanceMeters(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value) <= RepeatGpsMaxMeters;

        return builtUp == true && land == true && MunicipalityMatches(a.Municipality, b.Municipality);
    }

    public static bool TypesCompatible(PropertyType a, PropertyType b)
        => a == b || CompatibleTypes.Contains((a, b));

    /// <summary>Titulky se shodují po normalizaci a jsou dost dlouhé, aby to nebyla šablona.</summary>
    public static bool TitlesMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var na = NormalizeText(a);
        var nb = NormalizeText(b);
        if (na.Length < TitleEvidenceMinLength || nb.Length < TitleEvidenceMinLength) return false;
        // Bazoš titulek ořezává na 60 znaků – stačí, když je jeden prefixem druhého
        return na == nb || (Math.Min(na.Length, nb.Length) >= 45 && (na.StartsWith(nb, StringComparison.Ordinal) || nb.StartsWith(na, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Popisy jsou tentýž text: po normalizaci (bez diakritiky, interpunkce a zalomení řádků) je aspoň
    /// 90 % pětic slov kratšího popisu i v delším. Měří se ke kratšímu, protože portály text ořezávají
    /// nebo přidávají úvodní odstavec.
    /// </summary>
    public static bool DescriptionsMatch(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var sa = DescriptionShingleCache.GetValue(a, DescriptionShingles);
        var sb = DescriptionShingleCache.GetValue(b, DescriptionShingles);
        if (sa.Count < DescriptionMinShingles || sb.Count < DescriptionMinShingles) return false;
        var (smaller, larger) = sa.Count <= sb.Count ? (sa, sb) : (sb, sa);
        var common = smaller.Count(larger.Contains);
        return common >= smaller.Count * DescriptionMatchMin;
    }

    private static HashSet<int> DescriptionShingles(string description)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in NormalizeText(description))
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); continue; }
            if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());

        var array = words.ToArray();
        var shingles = new HashSet<int>();
        for (var i = 0; i + DescriptionShingleWords <= array.Length; i++)
            shingles.Add(string.Join(' ', array, i, DescriptionShingleWords).GetHashCode(StringComparison.Ordinal));
        return shingles;
    }

    /// <summary>„3+KK" = „3+kk"; prázdná dispozice se neshoduje s ničím.</summary>
    public static bool DispositionsMatch(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

    private static bool DistrictsDiffer(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && NormalizeText(a) != NormalizeText(b);

    /// <summary>
    /// Obce se shodují, když má některá část („ulice, obec" / „obec, okresní město" – Realingo,
    /// REALmix) po normalizaci protějšek. Bez diakritiky, bez velikosti písmen.
    /// </summary>
    public static bool MunicipalityMatches(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var partsA = MunicipalityParts(a);
        var partsB = MunicipalityParts(b);
        return partsA.Overlaps(partsB);
    }

    private static bool DistrictMatches(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && NormalizeText(a) == NormalizeText(b);

    private static HashSet<string> MunicipalityParts(string value)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in value.Split([',', '–', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = NormalizeText(raw);
            if (n.Length >= 3) parts.Add(n);
        }
        parts.Add(NormalizeText(value));
        return parts;
    }

    /// <summary>
    /// Malá písmena, bez diakritiky, sjednocené mezery – pro porovnání obcí, okresů, titulků a popisů.
    /// Kompatibilní rozklad sjednotí i „m²" a „m2" (Bazoš vs. Reality Čechy u téhož titulku).
    /// </summary>
    public static string NormalizeText(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Sestaví skupiny duplikátů (union-find) a vrátí mapu duplikát → primární inzerát.
    /// Primární = nejstarší <c>FirstSeenAt</c> ve skupině (má nejdelší cenovou historii);
    /// remíza se rozhoduje podle Id, aby byl výsledek deterministický.
    /// </summary>
    public static Dictionary<Guid, Guid> BuildClusters(IReadOnlyList<DuplicateCandidate> candidates)
    {
        var byId = candidates.ToDictionary(c => c.Id);

        // Krok 0: opakování v rámci zdroje (SREALITY má týž dům i 4×). Bez toho by
        // „jednoznačná shoda v cizím zdroji" nikdy nenastala a celá skupina by propadla.
        // Opakování se sloučí na zástupce (nejstarší) a do cross-source párování jde jen on.
        var repeatOf = BuildSameSourceRepeats(candidates);
        var representatives = candidates.Where(c => !repeatOf.ContainsKey(c.Id)).ToList();

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

        // Kandidáty porovnáváme jen uvnitř nabídky a jen v cenovém okně ±2 %
        // – z O(n²) přes všechno je O(n²) přes pár desítek inzerátů se stejnou cenou.
        // Typ nemovitosti se kontroluje až v páru (chata/dům apod. jsou kompatibilní).
        var pairs = new List<(DuplicateCandidate A, DuplicateCandidate B)>();
        foreach (var group in representatives.Where(c => c.Price is > 0).GroupBy(c => c.OfferType))
        {
            var sorted = group.OrderBy(c => c.Price).ToList();
            for (var i = 0; i < sorted.Count; i++)
            {
                var a = sorted[i];
                // Okno podle širší tolerance (kopie se zpožděnou cenou); o shodě rozhodne IsDuplicatePair
                var maxPrice = (double)a.Price!.Value / (1 - LaggingPriceTolerance);
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
        var clusters = parent.Keys.GroupBy(Find);
        var result = new Dictionary<Guid, Guid>();

        foreach (var cluster in clusters)
        {
            var members = cluster.Select(id => byId[id]).ToList();
            if (members.Count < 2) continue;

            // Dva zástupci ze stejného zdroje ve skupině = řetěz přes různé nemovitosti; radši nic
            if (members.DistinctBy(m => m.SourceId).Count() < members.Count) continue;

            var primary = members.OrderBy(m => m.FirstSeenAt).ThenBy(m => m.Id).First();
            foreach (var m in members.Where(m => m.Id != primary.Id))
                result[m.Id] = primary.Id;
        }

        // Opakování dostanou primár svého zástupce (nebo zástupce samotného, když nemá skupinu)
        foreach (var (dupId, repId) in repeatOf)
            result[dupId] = result.TryGetValue(repId, out var primaryId) ? primaryId : repId;

        return result;
    }

    /// <summary>
    /// Připojí stažené inzeráty s uživatelovým záznamem k živé kopii téhož domu: mapa stažený → primár
    /// skupiny, do které patří jeho aktivní protějšek (jiný portál, nebo znovu vložený inzerát téhož
    /// zdroje). Jen při jednoznačné shodě – když protějšky vedou do dvou různých skupin, nepřipojí se nic.
    /// Aktivní párování se tím nemění; běží nad hotovou mapou z <see cref="BuildClusters"/>.
    /// </summary>
    public static Dictionary<Guid, Guid> AttachRemembered(
        IReadOnlyList<DuplicateCandidate> active,
        IReadOnlyList<DuplicateCandidate> remembered,
        IReadOnlyDictionary<Guid, Guid> mapping)
    {
        var result = new Dictionary<Guid, Guid>();
        if (remembered.Count == 0) return result;

        var activeByOffer = active.Where(c => c.Price is > 0).ToLookup(c => c.OfferType);
        foreach (var gone in remembered.Where(c => c.Price is > 0))
        {
            var price = (double)gone.Price!.Value;
            var groups = new HashSet<Guid>();
            foreach (var live in activeByOffer[gone.OfferType])
            {
                var livePrice = (double)live.Price!.Value;
                if (Math.Abs(livePrice - price) > Math.Max(livePrice, price) * LaggingPriceTolerance) continue;
                if (IsDuplicatePair(gone, live) || IsSameSourceRepeat(gone, live))
                    groups.Add(mapping.TryGetValue(live.Id, out var primaryId) ? primaryId : live.Id);
            }

            if (groups.Count == 1)
                result[gone.Id] = groups.First();
        }
        return result;
    }

    /// <summary>Mapa opakovaný inzerát → zástupce (nejstarší v rámci zdroje). Union-find nad IsSameSourceRepeat.</summary>
    public static Dictionary<Guid, Guid> BuildSameSourceRepeats(IReadOnlyList<DuplicateCandidate> candidates)
    {
        var parent = new Dictionary<Guid, Guid>();

        Guid Find(Guid x)
        {
            while (parent.TryGetValue(x, out var p) && p != x)
            {
                parent[x] = parent.TryGetValue(p, out var gp) ? gp : p;
                x = parent[x];
            }
            return x;
        }

        foreach (var group in candidates.Where(c => c.Price is > 0).GroupBy(c => (c.SourceId, c.OfferType, c.Price)))
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            for (var i = 0; i < members.Count; i++)
                for (var j = i + 1; j < members.Count; j++)
                    if (IsSameSourceRepeat(members[i], members[j]))
                    {
                        var (rx, ry) = (Find(members[i].Id), Find(members[j].Id));
                        if (rx != ry) { parent.TryAdd(rx, rx); parent.TryAdd(ry, ry); parent[ry] = rx; }
                    }
        }

        var byId = candidates.ToDictionary(c => c.Id);
        var result = new Dictionary<Guid, Guid>();
        foreach (var cluster in parent.Keys.GroupBy(Find))
        {
            var members = cluster.Select(id => byId[id]).ToList();
            if (members.Count < 2) continue;
            // Zástupce = nejúplnější data (GPS, plochy), pak nejstarší – s ním se páruje napříč zdroji,
            // a kopie se špatně vytaženou výměrou by shodu zmařila.
            var rep = members
                .OrderByDescending(m => m.Latitude is not null && m.PreciseGps)
                .ThenByDescending(m => m.Latitude is not null)
                .ThenByDescending(m => m.AreaBuiltUp is > 0)
                .ThenByDescending(m => m.AreaLand is > 0)
                .ThenBy(m => m.FirstSeenAt).ThenBy(m => m.Id).First();
            foreach (var m in members.Where(m => m.Id != rep.Id))
                result[m.Id] = rep.Id;
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
