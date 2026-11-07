using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Projekce inzerátu pro párování duplikátů – jen skalární pole, žádné navigace.
/// Public kvůli unit testům čisté párovací logiky.
/// <para><see cref="SharedGps"/>, <see cref="TemplateTitle"/> a <see cref="TemplateDescription"/> doplňuje
/// <see cref="DuplicateDetectionService.ComputeMapping"/> z celé sady kandidátů (špendlík / text
/// sdílený víc jednotkami jednoho zdroje); <see cref="LastSeenAt"/> = konec běhu inzerátu.</para>
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
    bool IsActive = true,
    DateTime? LastSeenAt = null,
    bool SharedGps = false,
    bool TemplateDescription = false,
    bool TemplateTitle = false);

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

    /// <summary>Jak dlouho po stažení se inzerát ještě připojuje k živé kopii (přeinzerování, jiný portál).</summary>
    private const int RememberedWindowDays = 365;

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

    /// <summary>
    /// Jak dlouho smí nový inzerát v témže zdroji běžet vedle starého, aby to ještě bylo přeinzerování
    /// téhož domu: starý visí, než ho portál stáhne (Kratochvilka 6,5 dne, Strachotín 8 dní, Bazoš
    /// Šatov se slevou 3,79 → 3,64 mil. 21 dní). Déle souběžně = dvě různé jednotky (Tasovice, Bílé
    /// domy: domy developera běžely vedle sebe od února do října).
    /// </summary>
    private const int RelistOverlapDays = 30;

    /// <summary>Body blíž než tohle jsou tentýž špendlík (obec ze Sreality, místo developera).</summary>
    private const double SharedPinMeters = 5;

    /// <summary>Nový inzerát s opravenou plochou musí přijít do týdne od konce starého.</summary>
    private const int CorrectedAreaRelistDays = 7;

    /// <summary>Plochy přeinzerovaného domu v témže zdroji se musí shodovat na metr.</summary>
    private const double RelistAreaToleranceM2 = 1;

    /// <summary>Počet min-hashů pro hledání stejných popisů v rámci zdroje (šablony developera).</summary>
    private const int TemplateMinHashes = 6;

    private static readonly ConditionalWeakTable<string, HashSet<int>> DescriptionShingleCache = new();

    /// <summary>
    /// Zdroje, které posílají GPS, ale je to jen střed obce geokódovaný portálem
    /// (medián odchylky proti Sreality 0,8–3 km, měřeno 30. 9. 2026). Bereme je jako přibližné.
    /// UlovDomov sem nepatří: proti Sreality má medián 21 m a 90 % dvojic do 300 m (339 dvojic, 6. 10. 2026).
    /// </summary>
    public static readonly string[] ApproxGpsSources = ["REALITYCECHY", "REALITYMIX", "REALCITY", "BEZREALITKY"];

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
        // Aktivní inzeráty + stažené z posledního roku a stažené, ke kterým má někdo vlastní záznam
        // (stav, poznámky, fotky z prohlídky, hlas, pořadí, prohlídka). Stažené se nepárují mezi
        // sebou, jen se připojí ke své živé kopii – dům, kde už uživatel byl, se jinak po návratu
        // na jiný portál tváří jako neviděný, a přeinzerovaný dům (Kubšice: Sreality 5,7 mil.
        // stažen 10. 10., 12. 10. znovu za 5,4 mil.) by ztratil historii ceny i dny na trhu.
        var rememberedSince = DateTime.UtcNow.AddDays(-RememberedWindowDays);
        var all = await ctx.Listings
            .AsNoTracking()
            .Where(l => l.IsActive
                        || (l.DeactivatedAt != null && l.DeactivatedAt >= rememberedSince)
                        || l.UserStates.Any(s => s.Status != "New" || (s.Notes != null && s.Notes != ""))
                        || ctx.UserListingPhotos.Any(p => p.ListingId == l.Id)
                        || ctx.HouseVotes.Any(v => v.ListingId == l.Id)
                        || ctx.HouseRanks.Any(r => r.ListingId == l.Id)
                        || ctx.HouseVisits.Any(v => v.ListingId == l.Id))
            .Select(l => new DuplicateCandidate(
                l.Id, l.SourceId, l.PropertyType, l.OfferType,
                l.Price, l.Latitude, l.Longitude,
                l.Municipality, l.AreaBuiltUp, l.AreaLand, l.FirstSeenAt,
                l.GeocodeSource != "nominatim" && !ApproxGpsSources.Contains(l.SourceCode),
                l.District, l.Title, l.Disposition,
                l.Price >= ExactPriceEvidenceMin || l.Price == null ? l.Description : null,
                l.IsActive,
                l.LastSeenAt))
            .ToListAsync(cancellationToken);

        var mapping = ComputeMapping(all, out var rememberedCount);
        var activeCount = all.Count(c => c.IsActive);

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
            activeCount, clusters, mapping.Count, rememberedCount);

        return new DuplicateScanResultDto(activeCount, clusters, mapping.Count);
    }

    /// <summary>
    /// Celé párování nad načtenými kandidáty (aktivní + stažené): mapa duplikát → primár.
    /// Nejdřív se z celé sady (i stažených) označí špendlíky a popisy, které sdílí víc různých
    /// jednotek jednoho zdroje – ty nejsou důkazem, že jde o tentýž dům. Pak se shlukují aktivní
    /// (<see cref="BuildClusters"/>) a stažené se připojí k živé kopii (<see cref="AttachRemembered"/>).
    /// </summary>
    public static Dictionary<Guid, Guid> ComputeMapping(IReadOnlyList<DuplicateCandidate> all, out int rememberedCount)
    {
        all = MarkSharedEvidence(all);
        var candidates = all.Where(c => c.IsActive).ToList();
        var mapping = BuildClusters(candidates); // dupId -> primaryId

        var remembered = AttachRemembered(candidates, all.Where(c => !c.IsActive).ToList(), mapping);
        foreach (var (rememberedId, primaryId) in remembered)
            mapping[rememberedId] = primaryId;

        rememberedCount = remembered.Count;
        return mapping;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá párovací logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rozhodne, zda dva inzeráty popisují tutéž nemovitost.
    /// Nutné podmínky: jiný zdroj, stejný typ nemovitosti i nabídky, cena v toleranci 2 %.
    /// Plus jedna z evidencí: stejná cena na korunu a stejný dlouhý titulek nebo popis (ne šablona
    /// developera), NEBO přesná GPS obou do 300 m (sdílený špendlík jen se shodnými plochami a
    /// dispozicí), NEBO (bez přesné GPS) stejná cena na korunu + plochy bez rozporu + (GPS do 300 m,
    /// NEBO shodná plocha a GPS do 5 km či stejná obec).
    /// Opakování a přeinzerování v jednom zdroji řeší <see cref="IsSameSourceRepeat"/> a
    /// <see cref="IsSameSourceRelisting"/>.
    /// </summary>
    public static bool IsDuplicatePair(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.SourceId == b.SourceId) return false;
        if (!TypesCompatible(a.PropertyType, b.PropertyType)) return false;
        if (a.OfferType != b.OfferType) return false;

        if (a.Price is not > 0 || b.Price is not > 0) return IsPricelessCopy(a, b);
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
        if (priceDiff == 0 && a.Price >= ExactPriceEvidenceMin && OwnTitlesMatch(a, b)
            && !DistrictsDiffer(a.District, b.District))
            return true;

        // Evidence 0b: stejný popis a cena na korunu – realitka vloží tentýž text na Sreality i Bazoš
        // pod jiným titulkem a Bazoš z něj vytáhne jiné výměry (Prosiměřice: 237/929 vs. 207/722 m²).
        // Šablona, kterou zdroj použil pro víc různých jednotek, nedokazuje nic (Tasovice, Bílé domy).
        if (priceDiff == 0 && a.Price >= ExactPriceEvidenceMin && !DistrictsDiffer(a.District, b.District)
            && OwnDescriptionsMatch(a, b))
            return true;

        // Evidence 1: přesná GPS od zdroje u obou. Do 300 m je to tentýž dům, nad 1,5 km určitě ne
        // (dva inzeráty 2 km od sebe nejsou tentýž dům, ani když sedí cena, obec i plocha).
        // Mezi tím rozhodují přísnější důkazy níž – špendlík realitky bývá o stovky metrů vedle.
        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        if (distance <= GpsMaxMeters && a.PreciseGps && b.PreciseGps)
        {
            if (!a.SharedGps && !b.SharedGps) return true;

            // Evidence 1b: sdílený špendlík (obec bez adresy, místo developera, dům s víc byty) říká
            // jen „někde tady". S cenou v toleranci platí, jen když sedí plochy (aspoň jedna, žádná
            // v rozporu) i dispozice – jinak by se slily domy jednoho projektu (Tasovice: 100 vs. 129 m²,
            // pozemek 594 vs. 918 m², 3+kk vs. 4+kk na témže bodě).
            if (UnitDataAgree(a, b))
                return true;
        }

        // Evidence 2 (GPS chybí, nebo je jen geokódovaná z obce/PSČ): cena na korunu stejná
        // a plochy si neodporují. Přísnější než GPS větev, protože „7 490 000 Kč ve Znojmě"
        // můžou být dva různé domy.
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
    /// Kopie bez ceny („cena dohodou", „informace v RK" – Realcity 34 z 267 aktivních, Sreality ~900).
    /// Bez ceny se inzerát do párování vůbec nedostal, i když měl na jiném portálu slovo od slova stejný
    /// popis (Oslavany, Havířská 220 m²: Realcity bez ceny, Sreality/iDNES/Reas 7 999 999 Kč). Chybí-li
    /// cena na kterékoli straně, musí sedět všechno ostatní: okres bez rozporu, stejná obec nebo GPS do
    /// 5 km (dvě přesné GPS nad 1,5 km vylučují), plochy bez rozporu a aspoň jedna shodná (nebo stejná
    /// dispozice) a k tomu tentýž popis nebo dlouhý titulek. Jednoznačnost v cizím zdroji hlídá
    /// <see cref="BuildClusters"/> – šablonové byty developera bez ceny se tak neslijí.
    /// </summary>
    public static bool IsPricelessCopy(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.SourceId == b.SourceId) return false;
        if (!TypesCompatible(a.PropertyType, b.PropertyType) || a.OfferType != b.OfferType) return false;
        if (DistrictsDiffer(a.District, b.District)) return false;

        double? distance =
            a.Latitude is not null && a.Longitude is not null &&
            b.Latitude is not null && b.Longitude is not null
                ? GpsDistanceMeters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value)
                : null;
        if (distance > PreciseGpsRejectMeters && a.PreciseGps && b.PreciseGps) return false;
        if (!(distance <= ApproxGpsMaxMeters) && !MunicipalityMatches(a.Municipality, b.Municipality)) return false;

        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        if (builtUp == false || land == false) return false;
        if (builtUp != true && land != true && !DispositionsMatch(a.Disposition, b.Disposition)) return false;

        return OwnTitlesMatch(a, b) || OwnDescriptionsMatch(a, b);
    }

    /// <summary>
    /// Stejný dlouhý titulek. Šablona (titulek, pod kterým zdroj vedl víc různých jednotek – Těšetice:
    /// „Patrová novostavba 4+kk 125 m², pozemek 395 m²" u domů s pozemkem 367 i 395 m²) platí, jen když
    /// k ní sedí plochy, nebo je cena na korunu nekulatá.
    /// </summary>
    private static bool OwnTitlesMatch(DuplicateCandidate a, DuplicateCandidate b)
        => TitlesMatch(a.Title, b.Title)
           && (!a.TemplateTitle && !b.TemplateTitle || UnitDataAgree(a, b));

    /// <summary>
    /// Stejný popis. Šablona developera (tentýž text u víc různých jednotek – Tasovice: domy s pozemkem
    /// 594 i 600 m²) platí, jen když k ní sedí plochy, nebo je cena na korunu nekulatá.
    /// </summary>
    private static bool OwnDescriptionsMatch(DuplicateCandidate a, DuplicateCandidate b)
        => DescriptionsMatch(a.Description, b.Description)
           && (!a.TemplateDescription && !b.TemplateDescription || UnitDataAgree(a, b));

    /// <summary>
    /// Data jednotky si odpovídají – to, co musí doplnit sdílený špendlík nebo text sdílený víc jednotkami:
    /// plochy bez rozporu (aspoň jedna shodná) a dispozice bez rozporu, nebo nekulatá cena na korunu
    /// (8 870 803 Kč – cena spočítaná z metrů jednotky). Užitnou plochu, kterou jeden portál sečetl
    /// i s terasou (RealityMIX: 165 m² u „2+kk 40 m² + 125 m² zahrada"), nebo kterou nevyplnil, doplní
    /// plocha uvedená v titulku druhého inzerátu.
    /// </summary>
    private static bool UnitDataAgree(DuplicateCandidate a, DuplicateCandidate b)
    {
        if (a.Price is > 0 && a.Price == b.Price && a.Price % 1000 != 0) return true;
        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        if (builtUp != true && (TitleNamesArea(a.Title, b.AreaBuiltUp) || TitleNamesArea(b.Title, a.AreaBuiltUp)))
            builtUp = true;
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        return builtUp != false && land != false && (builtUp == true || land == true)
               && !DispositionsConflict(a.Disposition, b.Disposition);
    }

    /// <summary>
    /// Stažená kopie z jiného portálu si s živou nesmí odporovat: aspoň jedna plocha shodná, nebo žádná
    /// v rozporu; jiná dispozice jen s plochou shodnou na metr (různé portály píšou dispozici domu různě,
    /// 5+2 vs. 2+1 u téhož domu). Aktivní dvojice hlídá jednoznačnost v cizím zdroji, staženou ne – bez
    /// toho se k živému bytu za 22 500 Kč připojily stažené byty 40 i 78 m² do 300 m. Tentýž text
    /// (ne šablona) s cenou na korunu platí i přes rozpor ploch – Bazoš výměry z textu vytahuje špatně.
    /// </summary>
    private static bool GoneCopyFits(DuplicateCandidate gone, DuplicateCandidate live)
    {
        if (gone.Price is >= ExactPriceEvidenceMin && gone.Price == live.Price
            && !DistrictsDiffer(gone.District, live.District)
            && (OwnTitlesMatch(gone, live) || OwnDescriptionsMatch(gone, live)))
            return true;

        var builtUp = CompareAreas(gone.AreaBuiltUp, live.AreaBuiltUp);
        if (builtUp != true && (TitleNamesArea(gone.Title, live.AreaBuiltUp) || TitleNamesArea(live.Title, gone.AreaBuiltUp)))
            builtUp = true;
        var land = CompareAreas(gone.AreaLand, live.AreaLand);
        var exactArea = AreasEqualToMeter(gone.AreaBuiltUp, live.AreaBuiltUp) == true
                        || AreasEqualToMeter(gone.AreaLand, live.AreaLand) == true;
        if (DispositionsConflict(gone.Disposition, live.Disposition) && !exactArea) return false;
        return builtUp == true || land == true || (builtUp != false && land != false);
    }

    private static readonly Regex TitleAreaPattern = new(@"(\d+(?:[,.]\d+)?)\s*m(?:²|2)(?![\p{L}\d])", RegexOptions.Compiled);

    /// <summary>Titulek uvádí plochu v toleranci 5 % („Prodej bytu 1+kk 35,3 m² + 7,49 m² balkón" → 35,3).</summary>
    private static bool TitleNamesArea(string? title, double? area)
    {
        if (string.IsNullOrWhiteSpace(title) || area is not > 0) return false;
        foreach (Match match in TitleAreaPattern.Matches(title.Replace("\u00A0", " ")))
        {
            if (double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && CompareAreas(value, area) == true)
                return true;
        }
        return false;
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
        if (a.Price >= ExactPriceEvidenceMin && OwnTitlesMatch(a, b))
            return true;

        var builtUp = CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp);
        var land = CompareAreas(a.AreaLand, b.AreaLand);
        if (builtUp == false || land == false) return false;
        if (builtUp != true && land != true) return false;

        var bothPreciseGps = a.PreciseGps && b.PreciseGps
            && a.Latitude is not null && a.Longitude is not null
            && b.Latitude is not null && b.Longitude is not null;
        if (bothPreciseGps)
        {
            // Na sdíleném špendlíku (dům s víc jednotkami, místo developera) stojí i sousední jednotky
            // téže ceny – opakování tam chce plochy na metr (pozemek 594 vs. 600 m² jsou dva domy).
            if ((a.SharedGps || b.SharedGps)
                && (AreasEqualToMeter(a.AreaBuiltUp, b.AreaBuiltUp) == false || AreasEqualToMeter(a.AreaLand, b.AreaLand) == false))
                return false;
            return GpsDistanceMeters(a.Latitude!.Value, a.Longitude!.Value, b.Latitude!.Value, b.Longitude!.Value) <= RepeatGpsMaxMeters;
        }

        return builtUp == true && land == true && MunicipalityMatches(a.Municipality, b.Municipality);
    }

    /// <summary>
    /// Stažený inzerát a novější inzerát téhož zdroje jsou přeinzerovaný dům (Kubšice: Sreality 5,7 mil.
    /// naposledy 8. 10., od 12. 10. znovu za 5,4 mil. a místo 3+1 jako 4+1). Opakování
    /// (<see cref="IsSameSourceRepeat"/>) chce cenu na korunu, přeinzerování ale bývá se slevou, proto
    /// tu platí cena do 10 % – a za to přísně všechno ostatní:
    /// <list type="bullet">
    /// <item>starý skončil dřív, než nový začal (souběh nejvýš 30 dní) – dva inzeráty jednoho zdroje
    /// běžící vedle sebe déle jsou dvě jednotky (Tasovice, Bílé domy: pět domů jednoho developera);</item>
    /// <item>plochy na metr: žádná v rozporu, aspoň jedna známá u obou (100 vs. 129 m² ani pozemek
    /// 594 vs. 918 m² projít nesmí);</item>
    /// <item>dispozice stejná; jinou (makléř přepsal 3+1 na 4+1) unesou jen obě plochy na metr;</item>
    /// <item>poloha: přesná GPS obou (ne sdílený špendlík) do 100 m; bez ní stejný dlouhý titulek (Bazoš
    /// vkládá inzerát znovu), nebo stejná obec a k tomu obě plochy na metr, či cena na korunu a dispozice.</item>
    /// </list>
    /// Jedinou výjimkou z ploch je oprava překlepu (<see cref="IsCorrectedAreaRelisting"/>).
    /// </summary>
    public static bool IsSameSourceRelisting(DuplicateCandidate gone, DuplicateCandidate live)
    {
        if (gone.SourceId != live.SourceId || gone.Id == live.Id) return false;
        if (!TypesCompatible(gone.PropertyType, live.PropertyType) || gone.OfferType != live.OfferType) return false;
        if (gone.Price is not > 0 || live.Price is not > 0) return false;
        if (ActiveUntil(gone) > live.FirstSeenAt.AddDays(RelistOverlapDays)) return false;

        var maxPrice = (double)Math.Max(gone.Price.Value, live.Price.Value);
        if ((double)Math.Abs(gone.Price.Value - live.Price.Value) > maxPrice * LaggingPriceTolerance) return false;

        var builtUp = AreasEqualToMeter(gone.AreaBuiltUp, live.AreaBuiltUp);
        var land = AreasEqualToMeter(gone.AreaLand, live.AreaLand);
        if (builtUp == false || land == false) return IsCorrectedAreaRelisting(gone, live, builtUp, land);
        if (builtUp != true && land != true) return false;
        var bothAreas = builtUp == true && land == true;
        if (DispositionsConflict(gone.Disposition, live.Disposition) && !bothAreas) return false;

        if (HousePreciseGps(gone, live) is { } distance)
            return distance <= RepeatGpsMaxMeters;

        // Bez přesné polohy: znovu vložený inzerát se stejným dlouhým titulkem (Bazoš), nebo stejná obec
        // a k ní obě plochy na metr (dům), případně cena na korunu a stejná dispozice (byt nemá pozemek).
        if (OwnTitlesMatch(gone, live) && !DistrictsDiffer(gone.District, live.District))
            return true;
        if (!MunicipalityMatches(gone.Municipality, live.Municipality))
            return false;
        return bothAreas || (gone.Price == live.Price && DispositionsMatch(gone.Disposition, live.Disposition));
    }

    /// <summary>
    /// Makléř stáhl inzerát a hned ho vložil znovu s opravenou jednou plochou (Pozořice: „289 m², pozemek
    /// 289 m²" do 15. 9., od 16. 9. „210 m², pozemek 289 m²" – cena, GPS, dispozice i popis stejné).
    /// Platí jen bez známek developerského projektu: cena na korunu, druhá plocha na metr, nový inzerát do
    /// týdne, žádný sdílený špendlík ani šablona popisu, a přesná GPS do 100 m nebo stejná obec i popis.
    /// Tasovice (129 → 100 m² se stejným pozemkem a cenou) tak zůstanou dva domy – leží na špendlíku
    /// developera a mají jeho šablonu textu.
    /// </summary>
    private static bool IsCorrectedAreaRelisting(DuplicateCandidate gone, DuplicateCandidate live, bool? builtUp, bool? land)
    {
        if ((builtUp == false) == (land == false)) return false;  // přesně jedna plocha opravená…
        if (builtUp != true && land != true) return false;         // …a druhá na metr stejná
        if (gone.Price != live.Price) return false;
        var gap = live.FirstSeenAt - ActiveUntil(gone);
        if (gap > TimeSpan.FromDays(CorrectedAreaRelistDays) || gap < -TimeSpan.FromDays(CorrectedAreaRelistDays)) return false;
        if (gone.SharedGps || live.SharedGps || gone.TemplateDescription || live.TemplateDescription) return false;
        if (DispositionsConflict(gone.Disposition, live.Disposition)) return false;

        if (HousePreciseGps(gone, live) is { } distance)
            return distance <= RepeatGpsMaxMeters;
        return MunicipalityMatches(gone.Municipality, live.Municipality) && DescriptionsMatch(gone.Description, live.Description);
    }

    /// <summary>Vzdálenost dvou přesných GPS, které nejsou sdíleným špendlíkem; jinak null.</summary>
    private static double? HousePreciseGps(DuplicateCandidate a, DuplicateCandidate b)
        => a.PreciseGps && b.PreciseGps && !a.SharedGps && !b.SharedGps
           && a.Latitude is not null && a.Longitude is not null && b.Latitude is not null && b.Longitude is not null
            ? GpsDistanceMeters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value)
            : null;

    /// <summary>
    /// Dva inzeráty jednoho zdroje běžely vedle sebe déle než 30 dní a nejde o totéž (cena, plochy,
    /// dispozice) – jsou to dvě různé jednotky, ne přeinzerování ani opakování téhož domu.
    /// </summary>
    public static bool RanSideBySide(DuplicateCandidate a, DuplicateCandidate b)
        => a.SourceId == b.SourceId && a.Id != b.Id
           && Overlap(a, b) > TimeSpan.FromDays(RelistOverlapDays)
           && !SameUnitData(a, b);

    /// <summary>Totožná data jednotky: cena na korunu, plochy bez rozporu, dispozice bez rozporu.</summary>
    private static bool SameUnitData(DuplicateCandidate a, DuplicateCandidate b)
        => a.Price == b.Price
           && CompareAreas(a.AreaBuiltUp, b.AreaBuiltUp) != false
           && CompareAreas(a.AreaLand, b.AreaLand) != false
           && !DispositionsConflict(a.Disposition, b.Disposition);

    private static DateTime ActiveUntil(DuplicateCandidate c) => c.LastSeenAt ?? c.FirstSeenAt;

    private static TimeSpan Overlap(DuplicateCandidate a, DuplicateCandidate b)
    {
        var start = a.FirstSeenAt > b.FirstSeenAt ? a.FirstSeenAt : b.FirstSeenAt;
        var endA = ActiveUntil(a);
        var endB = ActiveUntil(b);
        return (endA < endB ? endA : endB) - start;
    }

    /// <summary>true = shoda na metr, false = rozpor, null = u jednoho chybí.</summary>
    private static bool? AreasEqualToMeter(double? a, double? b)
        => a is > 0 && b is > 0 ? Math.Abs(a.Value - b.Value) <= RelistAreaToleranceM2 : null;

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
        return ShinglesMatch(
            DescriptionShingleCache.GetValue(a, DescriptionShingles),
            DescriptionShingleCache.GetValue(b, DescriptionShingles));
    }

    private static bool ShinglesMatch(HashSet<int> sa, HashSet<int> sb)
    {
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

    /// <summary>Obě dispozice známé a jiné (3+kk vs. 4+kk); chybějící není rozpor.</summary>
    private static bool DispositionsConflict(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && !DispositionsMatch(a, b);

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

    private static string? NormalizeDistrict(string? district)
        => string.IsNullOrWhiteSpace(district) ? null : NormalizeText(district);

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

        // Inzeráty bez ceny („cena dohodou") nemají cenové okno – porovnávají se se všemi zástupci
        // téže nabídky ve stejném okrese (bez okresu se všemi); o shodě rozhodne IsPricelessCopy.
        var byOfferDistrict = representatives.ToLookup(c => (c.OfferType, District: NormalizeDistrict(c.District)));
        foreach (var a in representatives.Where(c => c.Price is not > 0))
        {
            var district = NormalizeDistrict(a.District);
            var pool = district is null
                ? representatives.Where(c => c.OfferType == a.OfferType)
                : byOfferDistrict[(a.OfferType, district)].Concat(byOfferDistrict[(a.OfferType, null)]);
            foreach (var b in pool)
            {
                if (b.Id == a.Id) continue;
                // Dva bez ceny by se potkaly z obou stran – stačí jedna orientace
                if (b.Price is not > 0 && a.Id.CompareTo(b.Id) >= 0) continue;
                if (IsDuplicatePair(a, b))
                    pairs.Add((a, b));
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
    /// <para>V témže zdroji platí jen opakování (<see cref="IsSameSourceRepeat"/>) a přeinzerování
    /// (<see cref="IsSameSourceRelisting"/>), ne volnější pravidla pro kopie napříč portály. Skupina
    /// navíc nesmí dostat dva inzeráty jednoho zdroje, které běžely vedle sebe (<see cref="RanSideBySide"/>)
    /// – Tasovice: tři stažené domy developera se připojily k jednomu živému a detail ukazoval fotky
    /// i historii ceny cizích domů. Při takovém rozporu se stažený nepřipojí vůbec.</para>
    /// </summary>
    public static Dictionary<Guid, Guid> AttachRemembered(
        IReadOnlyList<DuplicateCandidate> active,
        IReadOnlyList<DuplicateCandidate> remembered,
        IReadOnlyDictionary<Guid, Guid> mapping)
    {
        var result = new Dictionary<Guid, Guid>();
        if (remembered.Count == 0) return result;

        Guid GroupOf(Guid id) => mapping.TryGetValue(id, out var primaryId) ? primaryId : id;

        var activeByOffer = active.Where(c => c.Price is > 0).ToLookup(c => c.OfferType);
        foreach (var gone in remembered.Where(c => c.Price is > 0))
        {
            var price = (double)gone.Price!.Value;
            var groups = new HashSet<Guid>();
            foreach (var live in activeByOffer[gone.OfferType])
            {
                var livePrice = (double)live.Price!.Value;
                if (Math.Abs(livePrice - price) > Math.Max(livePrice, price) * LaggingPriceTolerance) continue;
                var match = gone.SourceId == live.SourceId
                    ? IsSameSourceRepeat(gone, live) || IsSameSourceRelisting(gone, live)
                    : IsDuplicatePair(gone, live) && GoneCopyFits(gone, live);
                if (match)
                    groups.Add(GroupOf(live.Id));
            }

            if (groups.Count == 1)
                result[gone.Id] = groups.First();
        }

        if (result.Count == 0) return result;

        // Skupina = živí členové + připojení stažení. Dva inzeráty jednoho zdroje, které běžely vedle sebe,
        // nejsou tentýž dům – takový stažený do skupiny nepatří (nevíme, který z nich je ten pravý).
        var goneById = remembered.ToDictionary(c => c.Id);
        var targeted = result.Values.ToHashSet();
        var liveMembers = active
            .Where(c => targeted.Contains(GroupOf(c.Id)))
            .ToLookup(c => GroupOf(c.Id));
        var attachedMembers = result.ToLookup(kv => kv.Value, kv => goneById[kv.Key]);

        var conflicted = result
            .Where(kv =>
            {
                var gone = goneById[kv.Key];
                return liveMembers[kv.Value].Concat(attachedMembers[kv.Value]).Any(m => RanSideBySide(gone, m));
            })
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in conflicted)
            result.Remove(id);

        return result;
    }

    /// <summary>
    /// Označí kandidáty, jejichž GPS, popis nebo titulek sdílí víc různých jednotek jednoho zdroje – takový
    /// údaj neidentifikuje dům, jen místo nebo developera (<see cref="FindSharedPins"/>,
    /// <see cref="FindTemplateDescriptions"/>, <see cref="FindTemplateTitles"/>). Špendlík zůstává polohou
    /// (dvě přesné GPS nad 1,5 km shodu dál vylučují), jen přestane sám o sobě dokazovat tentýž dům;
    /// šablona textu platí jen spolu se shodnými daty jednotky.
    /// </summary>
    public static IReadOnlyList<DuplicateCandidate> MarkSharedEvidence(IReadOnlyList<DuplicateCandidate> all)
    {
        var pins = FindSharedPins(all);
        var descriptions = FindTemplateDescriptions(all);
        var titles = FindTemplateTitles(all);
        if (pins.Count == 0 && descriptions.Count == 0 && titles.Count == 0) return all;

        return all
            .Select(c =>
            {
                var pin = pins.Contains(c.Id);
                var description = descriptions.Contains(c.Id);
                var title = titles.Contains(c.Id);
                return pin || description || title
                    ? c with
                    {
                        SharedGps = c.SharedGps || pin,
                        TemplateDescription = c.TemplateDescription || description,
                        TemplateTitle = c.TemplateTitle || title,
                    }
                    : c;
            })
            .ToList();
    }

    /// <summary>
    /// Přesné GPS body, na kterých jeden zdroj vedl déle než 30 dní současně dvě různé jednotky
    /// (jiná cena, plochy nebo dispozice). Takový bod je střed obce (Sreality u inzerátu bez adresy dává
    /// <c>entity_type = municipality</c>) nebo místo developera (Tasovice, Bílé domy: 48,83610 N
    /// 16,15558 E pro všechny domy i pozemky v obci) – o shodě domu neříká nic. Vrací všechny inzeráty
    /// s přesnou GPS do 5 m od takového bodu, z kteréhokoli zdroje.
    /// </summary>
    public static HashSet<Guid> FindSharedPins(IReadOnlyList<DuplicateCandidate> all)
    {
        const double cellDegrees = 0.0001; // ~11 m × 7 m – víc než SharedPinMeters, stačí sousední buňky
        static (long, long) Cell(double lat, double lon) => ((long)Math.Floor(lat / cellDegrees), (long)Math.Floor(lon / cellDegrees));

        var located = all.Where(c => c.PreciseGps && c.Latitude is not null && c.Longitude is not null).ToList();
        var grid = located
            .GroupBy(c => Cell(c.Latitude!.Value, c.Longitude!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        IEnumerable<DuplicateCandidate> Near(DuplicateCandidate c)
        {
            var (cy, cx) = Cell(c.Latitude!.Value, c.Longitude!.Value);
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (grid.TryGetValue((cy + dy, cx + dx), out var cell))
                        foreach (var other in cell)
                            if (other.Id != c.Id && GpsDistanceMeters(c.Latitude!.Value, c.Longitude!.Value,
                                    other.Latitude!.Value, other.Longitude!.Value) <= SharedPinMeters)
                                yield return other;
        }

        var shared = new HashSet<Guid>();
        foreach (var a in located)
        {
            if (shared.Contains(a.Id)) continue;
            if (!Near(a).Any(b => RanSideBySide(a, b))) continue;
            shared.Add(a.Id);
            foreach (var other in Near(a))
                shared.Add(other.Id);
        }
        return shared;
    }

    /// <summary>
    /// Popisy, které jeden zdroj použil pro víc různých jednotek běžících vedle sebe – šablona developera
    /// („Nabízíme k prodeji rodinný dům 4+kk v obci Tasovice… hrubá stavba", tentýž text u domů s pozemkem
    /// 594 i 600 m² a u nového inzerátu se 100 m²). Shoda takového textu na jiném portálu nedokazuje, že jde
    /// o tentýž dům. Stejné popisy se hledají přes min-hash pětic slov (jen v rámci zdroje), označí se celá
    /// komponenta textů, ve které se aspoň dvě různé jednotky potkaly v čase.
    /// </summary>
    public static HashSet<Guid> FindTemplateDescriptions(IReadOnlyList<DuplicateCandidate> all)
    {
        var texts = all
            .Where(c => !string.IsNullOrWhiteSpace(c.Description))
            .Select(c => (Candidate: c, Shingles: DescriptionShingleCache.GetValue(c.Description!, DescriptionShingles)))
            .Where(t => t.Shingles.Count >= DescriptionMinShingles)
            .ToList();

        var parent = Enumerable.Range(0, texts.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        var buckets = new Dictionary<(Guid Source, int Seed, uint Min), List<int>>();
        for (var i = 0; i < texts.Count; i++)
        {
            for (var seed = 0; seed < TemplateMinHashes; seed++)
            {
                var min = uint.MaxValue;
                foreach (var shingle in texts[i].Shingles)
                    min = Math.Min(min, MixHash((uint)shingle, (uint)seed));
                var key = (texts[i].Candidate.SourceId, seed, min);
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
                list.Add(i);
            }
        }

        var compared = new HashSet<(int, int)>();
        var templateRoots = new List<int>();
        foreach (var list in buckets.Values)
        {
            for (var p = 0; p < list.Count; p++)
                for (var q = p + 1; q < list.Count; q++)
                {
                    var (x, y) = (list[p], list[q]);
                    if (!compared.Add((x, y))) continue;
                    if (!ShinglesMatch(texts[x].Shingles, texts[y].Shingles)) continue;
                    parent[Find(y)] = Find(x);
                    if (RanSideBySide(texts[x].Candidate, texts[y].Candidate))
                        templateRoots.Add(x);
                }
        }

        var templateComponents = templateRoots.Select(Find).ToHashSet();
        var result = new HashSet<Guid>();
        for (var i = 0; i < texts.Count; i++)
            if (templateComponents.Contains(Find(i)))
                result.Add(texts[i].Candidate.Id);
        return result;
    }

    /// <summary>
    /// Titulky, pod kterými jeden zdroj vedl vedle sebe víc různých jednotek: automatické titulky Sreality
    /// („Prodej rodinného domu 128 m², pozemek 600 m²") a titulky developera (Těšetice, Century 21:
    /// „Patrová novostavba 4+kk 125 m², pozemek 395 m²" u domů s pozemkem 367 i 395 m²). Titulek je
    /// šablona pro všechny zdroje, které ho použijí – Reality Čechy ho od Century 21 převzaly doslova.
    /// </summary>
    public static HashSet<Guid> FindTemplateTitles(IReadOnlyList<DuplicateCandidate> all)
    {
        var result = new HashSet<Guid>();
        var byTitle = all
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .GroupBy(c => NormalizeText(c.Title!))
            .Where(g => g.Key.Length >= TitleEvidenceMinLength);
        foreach (var group in byTitle)
        {
            var members = group.ToList();
            if (members.Count < 2) continue;
            var template = members
                .GroupBy(c => c.SourceId)
                .Any(source =>
                {
                    var list = source.ToList();
                    for (var i = 0; i < list.Count; i++)
                        for (var j = i + 1; j < list.Count; j++)
                            if (RanSideBySide(list[i], list[j])) return true;
                    return false;
                });
            if (template)
                result.UnionWith(members.Select(c => c.Id));
        }
        return result;
    }

    private static uint MixHash(uint value, uint seed)
    {
        var x = value ^ (seed * 0x9E3779B9u);
        x ^= x >> 16;
        x *= 0x85EBCA6Bu;
        x ^= x >> 13;
        x *= 0xC2B2AE35u;
        x ^= x >> 16;
        return x;
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
