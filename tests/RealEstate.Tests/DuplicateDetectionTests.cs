using RealEstate.Api.Services;
using RealEstate.Domain.Enums;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Detekce cross-source duplikátů – IsDuplicatePair + BuildClusters
//  Scénáře podle reálných případů: stejný dům na REMAX + iReality + SREALITY
//  se zobrazoval 3× s protichůdnými cenovými signály.
// ─────────────────────────────────────────────────────────────────
public class DuplicateDetectionPairTests
{
    private static readonly Guid SourceA = Guid.NewGuid();
    private static readonly Guid SourceB = Guid.NewGuid();

    private static DuplicateCandidate Make(
        Guid? source = null,
        PropertyType propertyType = PropertyType.House,
        OfferType offerType = OfferType.Sale,
        decimal? price = 7_490_000m,
        double? lat = 48.8555,
        double? lon = 16.0488,
        string? municipality = "Znojmo",
        double? areaBuiltUp = 314,
        double? areaLand = 673,
        int daysOld = 0,
        bool preciseGps = true)
        => new(
            Guid.NewGuid(), source ?? SourceA, propertyType, offerType,
            price, lat, lon, municipality, areaBuiltUp, areaLand,
            new DateTime(2026, 8, 1).AddDays(-daysOld), preciseGps);

    [Fact]
    public void SameHouse_TwoSources_SamePriceCloseGps_IsDuplicate()
    {
        // Znojmo, Kunštátská – 7 490 000 Kč na dvou zdrojích, geokódování se liší o ~50 m
        var remax = Make(source: SourceA);
        var sreality = Make(source: SourceB, lat: 48.8559, lon: 16.0492);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(remax, sreality));
    }

    [Fact]
    public void SameSource_NeverDuplicate()
    {
        var a = Make(source: SourceA);
        var b = Make(source: SourceA, lat: a.Latitude, lon: a.Longitude);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Theory]
    [InlineData(7_490_000, 7_490_000, true)]   // na korunu stejné
    [InlineData(7_490_000, 7_600_000, true)]   // ~1,5 % – v toleranci
    [InlineData(7_490_000, 7_700_000, false)]  // ~2,7 % – mimo toleranci
    [InlineData(7_490_000, 5_000_000, false)]  // úplně jiná cena
    public void PriceTolerance_TwoPercent(decimal priceA, decimal priceB, bool expected)
    {
        var a = Make(source: SourceA, price: priceA);
        var b = Make(source: SourceB, price: priceB);

        Assert.Equal(expected, DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GpsFarApart_SamePrice_NotDuplicate()
    {
        // Dva různé domy za stejnou cenu na opačných koncích Znojma (~2 km)
        var a = Make(source: SourceA);
        var b = Make(source: SourceB, lat: 48.8555 + 0.018, lon: 16.0488);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void DifferentPropertyOrOfferType_NotDuplicate()
    {
        var house = Make(source: SourceA);
        var land = Make(source: SourceB, propertyType: PropertyType.Land);
        var rent = Make(source: SourceB, offerType: OfferType.Rent);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(house, land));
        Assert.False(DuplicateDetectionService.IsDuplicatePair(house, rent));
    }

    [Fact]
    public void MissingGps_ExactPriceSameMunicipalityMatchingArea_IsDuplicate()
    {
        // Bazoš inzerát bez GPS ("671 63 Znojmo") vs. SREALITY s GPS – fallback větev
        var bazos = Make(source: SourceA, lat: null, lon: null, municipality: "Lechovice", areaBuiltUp: 129);
        var sreality = Make(source: SourceB, municipality: "Lechovice", areaBuiltUp: 129);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(bazos, sreality));
    }

    [Fact]
    public void MissingGps_ExactPriceDifferentMunicipality_NotDuplicate()
    {
        var a = Make(source: SourceA, lat: null, lon: null, municipality: "Znojmo");
        var b = Make(source: SourceB, lat: null, lon: null, municipality: "Lechovice");

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void MissingGps_PriceDiffersByCrown_NotDuplicate()
    {
        // Fallback bez GPS vyžaduje cenu na korunu – 2% tolerance by v jedné obci sloučila různé domy
        var a = Make(source: SourceA, lat: null, lon: null, price: 7_490_000m);
        var b = Make(source: SourceB, lat: null, lon: null, price: 7_500_000m);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void MissingGps_NoAreaData_NotDuplicate()
    {
        // Bez GPS i bez plochy není dost evidence – radši nechat oba
        var a = Make(source: SourceA, lat: null, lon: null, areaBuiltUp: null, areaLand: null);
        var b = Make(source: SourceB, lat: null, lon: null, areaBuiltUp: null, areaLand: null);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GeocodedGps_TwoKmAway_ExactPriceMatchingLand_IsDuplicate()
    {
        // Práče: Bazoš geokódovaný z "671 61 Znojmo" padl 2,3 km od domu, SREALITY má přesnou GPS
        var sreality = Make(source: SourceA, price: 5_990_000m, lat: 48.87560, lon: 16.20226,
            municipality: "Práče", areaBuiltUp: 180, areaLand: 820);
        var bazos = Make(source: SourceB, price: 5_990_000m, lat: 48.89415, lon: 16.18711,
            municipality: null, areaBuiltUp: null, areaLand: 820, preciseGps: false);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, bazos));
    }

    [Fact]
    public void GeocodedGps_Within300m_ExactPriceNoAreas_IsDuplicate()
    {
        // PRODEJMETO plochy vůbec neposílá – "127 m² - Šatov" vs. SREALITY, stejný bod
        var sreality = Make(source: SourceA, price: 3_790_000m, areaBuiltUp: 127, areaLand: 626);
        var prodejmeto = Make(source: SourceB, price: 3_790_000m, areaBuiltUp: null, areaLand: null, preciseGps: false);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, prodejmeto));
    }

    [Fact]
    public void GeocodedGps_OneKmAway_ExactPriceNoAreas_NotDuplicate()
    {
        var a = Make(source: SourceA, areaBuiltUp: null, areaLand: null);
        var b = Make(source: SourceB, lat: 48.8645, areaBuiltUp: null, areaLand: null, preciseGps: false);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GeocodedGps_AreaContradiction_NotDuplicate()
    {
        // Stejná cena i pozemek, ale dům 180 vs. 120 m² – dva různé domy v okolí
        var a = Make(source: SourceA, areaBuiltUp: 180, areaLand: 820);
        var b = Make(source: SourceB, lat: 48.8655, areaBuiltUp: 120, areaLand: 820, preciseGps: false);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GeocodedGps_PriceDiffersByCrown_NotDuplicate()
    {
        var a = Make(source: SourceA, price: 7_490_000m);
        var b = Make(source: SourceB, price: 7_500_000m, lat: 48.8655, preciseGps: false);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GeocodedGps_FarAwayDifferentMunicipality_NotDuplicate()
    {
        // ~11 km a jiná obec – ani geokódování z PSČ se tolik nemýlí
        var a = Make(source: SourceA, municipality: "Znojmo");
        var b = Make(source: SourceB, lat: 48.8555 + 0.1, municipality: "Lechovice", preciseGps: false);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void PreciseGps_TwoKmAway_StillNotDuplicate()
    {
        // Přesná GPS u obou dál rozhoduje sama – plocha ani cena na korunu to nepřebijí
        var a = Make(source: SourceA);
        var b = Make(source: SourceB, lat: 48.8555 + 0.018);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void MissingPrice_NotDuplicate()
    {
        var a = Make(source: SourceA, price: null);
        var b = Make(source: SourceB, price: null);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void GpsDistance_KnownPoints()
    {
        // ~111 m na 1/1000 stupně zeměpisné šířky
        var d = DuplicateDetectionService.GpsDistanceMeters(48.8555, 16.0488, 48.8565, 16.0488);
        Assert.InRange(d, 100, 120);
    }
}

public class DuplicateDetectionClusterTests
{
    private static readonly Guid SourceA = Guid.NewGuid();
    private static readonly Guid SourceB = Guid.NewGuid();
    private static readonly Guid SourceC = Guid.NewGuid();

    private static DuplicateCandidate Make(Guid source, int daysOld, decimal price = 7_490_000m,
        double lat = 48.8555, double lon = 16.0488, PropertyType propertyType = PropertyType.House)
        => new(
            Guid.NewGuid(), source, propertyType, OfferType.Sale,
            price, lat, lon, "Znojmo", propertyType == PropertyType.Land ? null : 314, 673,
            new DateTime(2026, 8, 1).AddDays(-daysOld));

    [Fact]
    public void ThreeSources_OldestBecomesPrimary()
    {
        // REMAX (nejstarší) + iReality + SREALITY → 1 skupina, 2 duplikáty na REMAX
        var remax = Make(SourceA, daysOld: 30);
        var ireality = Make(SourceB, daysOld: 10);
        var sreality = Make(SourceC, daysOld: 2);

        var mapping = DuplicateDetectionService.BuildClusters([sreality, remax, ireality]);

        Assert.Equal(2, mapping.Count);
        Assert.Equal(remax.Id, mapping[ireality.Id]);
        Assert.Equal(remax.Id, mapping[sreality.Id]);
        Assert.False(mapping.ContainsKey(remax.Id));
    }

    [Fact]
    public void TransitiveChain_AllPointToOldest()
    {
        // A↔B se páruje, B↔C se páruje, A↔C přímo ne (ceny 7.40M / 7.50M / 7.62M)
        // – union-find musí spojit celý řetěz na nejstarší A
        var a = Make(SourceA, daysOld: 30, price: 7_400_000m);
        var b = Make(SourceB, daysOld: 20, price: 7_500_000m);
        var c = Make(SourceC, daysOld: 10, price: 7_620_000m);
        Assert.True(DuplicateDetectionService.IsDuplicatePair(a, b));
        Assert.True(DuplicateDetectionService.IsDuplicatePair(b, c));
        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, c));

        var mapping = DuplicateDetectionService.BuildClusters([a, b, c]);

        Assert.Equal(2, mapping.Count);
        Assert.Equal(a.Id, mapping[b.Id]);
        Assert.Equal(a.Id, mapping[c.Id]);
    }

    [Fact]
    public void Subdivision_IdenticalParcelsOnTwoSources_NotMerged()
    {
        // Božice: pozemky č. 1–3, stejná cena, výměra i poloha – na Bazoši i SREALITY
        var bazos = Enumerable.Range(0, 3).Select(i => Make(SourceA, daysOld: 10 + i, propertyType: PropertyType.Land)).ToList();
        var sreality = Enumerable.Range(0, 3).Select(i => Make(SourceB, daysOld: i, propertyType: PropertyType.Land)).ToList();

        var mapping = DuplicateDetectionService.BuildClusters([.. bazos, .. sreality]);

        Assert.Empty(mapping);
    }

    [Fact]
    public void UniqueMatchSurvivesNextToAmbiguousOne()
    {
        // Jednoznačná dvojice se nesmí ztratit jen proto, že vedle je nejednoznačná parcelace
        var house1 = Make(SourceA, daysOld: 20);
        var house2 = Make(SourceB, daysOld: 5);
        var parcels = new[]
        {
            Make(SourceA, daysOld: 9, price: 2_160_000m, lat: 48.7550, lon: 16.2280, propertyType: PropertyType.Land),
            Make(SourceA, daysOld: 8, price: 2_160_000m, lat: 48.7550, lon: 16.2280, propertyType: PropertyType.Land),
            Make(SourceB, daysOld: 7, price: 2_160_000m, lat: 48.7550, lon: 16.2280, propertyType: PropertyType.Land),
        };

        var mapping = DuplicateDetectionService.BuildClusters([house1, house2, .. parcels]);

        Assert.Single(mapping);
        Assert.Equal(house1.Id, mapping[house2.Id]);
    }

    [Fact]
    public void ChainWithTwoListingsFromSameSource_Dropped()
    {
        // A(S1)–B(S2)–C(S3)–D(S1): každý pár jednoznačný, ale skupina by měla dva inzeráty z S1
        var a = Make(SourceA, daysOld: 40, price: 7_400_000m);
        var b = Make(SourceB, daysOld: 30, price: 7_500_000m);
        var c = Make(SourceC, daysOld: 20, price: 7_620_000m);
        var d = Make(SourceA, daysOld: 10, price: 7_740_000m);
        Assert.True(DuplicateDetectionService.IsDuplicatePair(c, d));
        Assert.False(DuplicateDetectionService.IsDuplicatePair(b, d));

        var mapping = DuplicateDetectionService.BuildClusters([a, b, c, d]);

        Assert.Empty(mapping);
    }

    [Fact]
    public void UnrelatedListings_NoClusters()
    {
        var a = Make(SourceA, daysOld: 5, price: 3_000_000m, lat: 48.90);
        var b = Make(SourceB, daysOld: 3, price: 5_000_000m, lat: 48.70);

        var mapping = DuplicateDetectionService.BuildClusters([a, b]);

        Assert.Empty(mapping);
    }

    [Fact]
    public void TwoIndependentClusters_EachHasOwnPrimary()
    {
        var h1a = Make(SourceA, daysOld: 20);
        var h1b = Make(SourceB, daysOld: 5);
        var h2a = Make(SourceA, daysOld: 15, price: 7_300_000m, lat: 48.7550, lon: 16.2280);
        var h2b = Make(SourceC, daysOld: 1, price: 7_300_000m, lat: 48.7552, lon: 16.2283);

        var mapping = DuplicateDetectionService.BuildClusters([h1a, h1b, h2a, h2b]);

        Assert.Equal(2, mapping.Count);
        Assert.Equal(h1a.Id, mapping[h1b.Id]);
        Assert.Equal(h2a.Id, mapping[h2b.Id]);
    }

    [Fact]
    public void SameSourceRepeats_CollapseAndFollowRepresentative()
    {
        // SREALITY: dům Kuchařovická 4× (stejná cena, plochy, GPS); Realingo má týž dům 1×.
        // Dřív „nejednoznačná shoda" celou skupinu zahodila – teď se opakování slijí na nejstarší.
        var sreality = Enumerable.Range(0, 4).Select(i => Make(SourceA, daysOld: 30 + i)).ToList();
        var realingo = Make(SourceB, daysOld: 2);

        var mapping = DuplicateDetectionService.BuildClusters([.. sreality, realingo]);

        var oldest = sreality.OrderBy(s => s.FirstSeenAt).First();
        Assert.Equal(4, mapping.Count);
        Assert.All(sreality.Where(s => s.Id != oldest.Id), s => Assert.Equal(oldest.Id, mapping[s.Id]));
        Assert.Equal(oldest.Id, mapping[realingo.Id]);
    }

    [Fact]
    public void SameSourceRepeat_TwoDifferentHousesFarApart_NotCollapsed()
    {
        var a = Make(SourceA, daysOld: 10);
        var b = Make(SourceA, daysOld: 5, lat: 48.8555 + 0.005);   // ~550 m

        Assert.False(DuplicateDetectionService.IsSameSourceRepeat(a, b));
        Assert.Empty(DuplicateDetectionService.BuildClusters([a, b]));
    }
}

public class DuplicateDetectionRelaxedRuleTests
{
    private static readonly Guid SourceA = Guid.NewGuid();
    private static readonly Guid SourceB = Guid.NewGuid();

    private static DuplicateCandidate Make(Guid source, PropertyType type = PropertyType.House,
        double? lat = null, double? lon = null, string? municipality = "Znojmo", string? district = "Znojmo",
        double? builtUp = 120, double? land = 558, bool preciseGps = true)
        => new(Guid.NewGuid(), source, type, OfferType.Sale, 6_690_000m, lat, lon, municipality, builtUp, land,
            new DateTime(2026, 9, 1), preciseGps, district);

    [Theory]
    [InlineData("Šanov, Znojmo", "Šanov")]              // Realingo: obec + okresní město
    [InlineData("Dlouhá, Hrabětice", "Hrabětice")]      // REALmix: ulice + obec
    [InlineData("náměstí Svobody, Znojmo", "Znojmo")]   // ulice + město
    [InlineData("Brno - Chrlice", "Brno")]              // Bezrealitky: město + část
    [InlineData("ZNOJMO", "znojmo")]                    // velikost písmen
    [InlineData("Zelešice", "Želešice")]                // diakritika
    public void MunicipalityMatches_NormalizedParts(string a, string b)
        => Assert.True(DuplicateDetectionService.MunicipalityMatches(a, b));

    [Theory]
    [InlineData("Šanov", "Šatov")]
    [InlineData("Znojmo", null)]
    [InlineData("", "Znojmo")]
    public void MunicipalityMatches_DifferentOrMissing_False(string? a, string? b)
        => Assert.False(DuplicateDetectionService.MunicipalityMatches(a, b));

    [Fact]
    public void NoGps_MunicipalityWithStreetPrefix_IsDuplicate()
    {
        var realmix = Make(SourceA, municipality: "Dlouhá, Hrabětice");
        var sreality = Make(SourceB, municipality: "Hrabětice");

        Assert.True(DuplicateDetectionService.IsDuplicatePair(realmix, sreality));
    }

    [Fact]
    public void NoGps_MunicipalityMissing_SameDistrictAndBothAreas_IsDuplicate()
    {
        // iDNES obec neplní; cena na korunu + užitná plocha + pozemek + okres stačí
        var idnes = Make(SourceA, municipality: null);
        var sreality = Make(SourceB);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(idnes, sreality));
    }

    [Fact]
    public void NoGps_MunicipalityMissing_LandUnknown_NotDuplicate()
    {
        var idnes = Make(SourceA, municipality: null, land: null);
        var sreality = Make(SourceB);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(idnes, sreality));
    }

    [Fact]
    public void NoGps_MunicipalityMissing_DifferentDistrict_NotDuplicate()
    {
        var a = Make(SourceA, municipality: null, district: "Brno-venkov");
        var b = Make(SourceB);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void CottageVsHouse_CloseGps_IsDuplicate()
    {
        var chata = Make(SourceA, PropertyType.Cottage, lat: 49.16, lon: 16.55);
        var dum = Make(SourceB, PropertyType.House, lat: 49.1601, lon: 16.5501);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(chata, dum));
    }

    [Fact]
    public void HouseVsLand_NeverDuplicate()
    {
        var dum = Make(SourceA, PropertyType.House, lat: 49.16, lon: 16.55);
        var pozemek = Make(SourceB, PropertyType.Land, lat: 49.16, lon: 16.55);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(dum, pozemek));
    }

    [Fact]
    public void ApproxGpsSource_FarCentroid_StillDuplicateByAreasAndMunicipality()
    {
        // Reality Čechy geokóduje střed obce (~800 m od domu); Sreality má přesnou GPS
        var realitycechy = Make(SourceA, lat: 48.86, lon: 16.09, preciseGps: false);
        var sreality = Make(SourceB, lat: 48.867, lon: 16.095);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(realitycechy, sreality));
    }

    [Fact]
    public void ApproxGpsSources_ContainsNewPortals()
    {
        Assert.Contains("REALITYCECHY", DuplicateDetectionService.ApproxGpsSources);
        Assert.DoesNotContain("SREALITY", DuplicateDetectionService.ApproxGpsSources);
    }
}

// ─────────────────────────────────────────────────────────────────
//  Jevišovice (1. 10. 2026): dům za 7,9 mil. na Sreality, Nemovitostech Znojmo a 2× na Bazoši
//  zůstal nespárovaný – web realitky nemá plochy a GPS má o 480 m vedle, druhá bazošová kopie
//  má jinou výměru. Dům za 4,718 mil. vedou Reality Čechy a REALmix jako dům i jako chalupu.
// ─────────────────────────────────────────────────────────────────
public class DuplicateDetectionJevisoviceTests
{
    private static readonly Guid Sreality = Guid.NewGuid();
    private static readonly Guid NemZnojmo = Guid.NewGuid();
    private static readonly Guid Bazos = Guid.NewGuid();
    private static readonly Guid RealityCechy = Guid.NewGuid();
    private const string LongTitle = "Prodej dvougeneračního rodinného domu s výhledem na zámek Jevišovice";

    private static DuplicateCandidate Make(Guid source, decimal price = 7_900_000m, double? lat = 48.9874, double? lon = 15.9899,
        string? municipality = "Jevišovice", double? builtUp = 350, double? land = 868, bool precise = true,
        string? title = "Prodej vícegeneračního domu 350 m², pozemek 868 m²", string? disposition = "3+KK",
        PropertyType type = PropertyType.House, int daysOld = 0)
        => new(Guid.NewGuid(), source, type, OfferType.Sale, price, lat, lon, municipality, builtUp, land,
            new DateTime(2026, 9, 1).AddDays(-daysOld), precise, "Znojmo", title, disposition);

    [Fact]
    public void NoAreas_Gps480m_SameDispositionAndExactPrice_IsDuplicate()
    {
        var sreality = Make(Sreality);
        var web = Make(NemZnojmo, lat: 48.9915, lon: 15.9879, municipality: null, builtUp: null, land: null, title: LongTitle);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, web));
    }

    [Fact]
    public void NoAreas_DifferentDisposition_NotDuplicate()
    {
        var sreality = Make(Sreality);
        var web = Make(NemZnojmo, lat: 48.9915, lon: 15.9879, municipality: null, builtUp: null, land: null, title: LongTitle, disposition: "5+1");

        Assert.False(DuplicateDetectionService.IsDuplicatePair(sreality, web));
    }

    [Fact]
    public void PreciseGps_Over1500m_StillRejected()
    {
        var a = Make(Sreality);
        var b = Make(NemZnojmo, lat: 48.9874 + 0.018, builtUp: null, land: null, municipality: null);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void SameLongTitle_ExactPrice_IsDuplicate_EvenWithConflictingLand()
    {
        var web = Make(NemZnojmo, builtUp: null, land: null, municipality: null, title: LongTitle);
        var bazos = Make(Bazos, lat: null, lon: null, municipality: null, builtUp: null, land: 450, title: LongTitle, disposition: "2+KK");

        Assert.True(DuplicateDetectionService.IsDuplicatePair(web, bazos));
    }

    [Theory]
    [InlineData("Prodej domu Znojmo", "Prodej domu Znojmo", false)]                    // moc krátký = šablona
    [InlineData("Prodej dvougeneračního rodinného domu s výhledem na zámek", "PRODEJ DVOUGENERAČNÍHO RODINNÉHO DOMU S VÝHLEDEM NA ZÁMEK", true)]
    [InlineData("Prodej dvougeneračního rodinného domu s výhledem na zámek Jevišovice", "Prodej dvougeneračního rodinného domu s výhledem n", true)]  // Bazoš ořezává
    [InlineData("Prodej dvougeneračního rodinného domu s výhledem na zámek", "Prodej prostorného rodinného domu se zahradou a garáží", false)]
    public void TitlesMatch_Cases(string a, string b, bool expected)
        => Assert.Equal(expected, DuplicateDetectionService.TitlesMatch(a, b));

    [Fact]
    public void SameSource_HouseAndCottage_AreOneRepeat()
    {
        var house = Make(RealityCechy, 4_718_000m, builtUp: 94, land: 202, precise: false, title: "prodej rodinného domu/chalupy (4+ KK), JEVIŠOVICE", disposition: "4+kk");
        var cottage = house with { Id = Guid.NewGuid(), PropertyType = PropertyType.Cottage };

        Assert.True(DuplicateDetectionService.IsSameSourceRepeat(house, cottage));
    }

    [Fact]
    public void FullScenario_AllCopiesEndInOneGroup()
    {
        var sreality = Make(Sreality, daysOld: 30);
        var web = Make(NemZnojmo, lat: 48.9915, lon: 15.9879, municipality: null, builtUp: null, land: null, title: LongTitle, daysOld: 20);
        var bazos1 = Make(Bazos, lat: 48.9879, lon: 16.0012, precise: false, municipality: null, builtUp: null, land: 868, title: LongTitle, daysOld: 10);
        var bazos2 = Make(Bazos, lat: null, lon: null, municipality: null, builtUp: null, land: 450, title: LongTitle, disposition: "2+KK", daysOld: 5);

        var mapping = DuplicateDetectionService.BuildClusters([sreality, web, bazos1, bazos2]);

        Assert.Equal(3, mapping.Count);
        Assert.All(new[] { web, bazos1, bazos2 }, c => Assert.Equal(sreality.Id, mapping[c.Id]));
    }

    [Fact]
    public void LaggingPrice_SameAreasDispositionDistrict_IsDuplicate()
    {
        // Znojmo centrum: Sreality 6 897 485 Kč, iDNES pořád 7 427 735 Kč (−7 %), jinak vše stejné
        var sreality = Make(Sreality, 6_897_485m, lat: 48.8557, lon: 16.0462, municipality: "Znojmo", builtUp: 150, land: 322, disposition: "3+1");
        var idnes = Make(NemZnojmo, 7_427_735m, lat: 48.8555, lon: 16.0488, municipality: null, builtUp: 150, land: 322, precise: false, disposition: "3+1");

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, idnes));
        Assert.Single(DuplicateDetectionService.BuildClusters([sreality, idnes]));
    }

    [Theory]
    [InlineData(150, 322, "4+1", 7_427_735)]   // jiná dispozice
    [InlineData(160, 322, "3+1", 7_427_735)]   // jiná užitná plocha
    [InlineData(150, 400, "3+1", 7_427_735)]   // jiný pozemek
    [InlineData(150, 322, "3+1", 7_900_000)]   // rozdíl ceny nad 10 %
    public void LaggingPrice_AnyMismatch_NotDuplicate(double builtUp, double land, string disposition, decimal price)
    {
        var sreality = Make(Sreality, 6_897_485m, lat: 48.8557, lon: 16.0462, municipality: "Znojmo", builtUp: 150, land: 322, disposition: "3+1");
        var other = Make(NemZnojmo, price, lat: 48.8555, lon: 16.0488, municipality: null, builtUp: builtUp, land: land, precise: false, disposition: disposition);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(sreality, other));
    }
}
