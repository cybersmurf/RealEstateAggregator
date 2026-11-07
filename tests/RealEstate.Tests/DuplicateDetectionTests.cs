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
    [InlineData("Prodej rodinného domu s garáží, sklepem a zahradou, 1223 m²,", "Prodej rodinného domu s garáží, sklepem a zahradou, 1223 m2, Šatov - Znojmo", true)]  // m² = m2
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

// ─────────────────────────────────────────────────────────────────
//  Prosiměřice (5. 10. 2026): dům za 7 999 000 Kč na Sreality a Bazoši zůstal dvakrát – titulky
//  se liší a Bazoš vytáhl z popisu jiné výměry (207/722 m² místo 237/929 m²). Popis je ale tentýž text.
// ─────────────────────────────────────────────────────────────────
public class DuplicateDetectionDescriptionTests
{
    private static readonly Guid Sreality = Guid.NewGuid();
    private static readonly Guid Bazos = Guid.NewGuid();

    private const string Intro =
        "V klidné části obce Prosiměřice nabízíme samostatně stojící rodinný dům o třech podlažích s podlahovou plochou přesahující 320 m².";

    private const string Body =
        "Technické a odpočinkové zázemí domu je soustředěno v 1. podzemním podlaží (85,6 m²), kde najdete garáž pro dvě vozidla (29,2 m²), technickou místnost (11,6 m²), kancelář a saunu. "
        + "Hlavní obytný prostor v 1. nadzemním podlaží působí vzdušně a přirozeně propojuje jednotlivé zóny. Kuchyně s jídelním koutem (23,3 m²) je orientována na jihozápad. "
        + "Ve 2. nadzemním podlaží se nachází tři samostatné, neprůchozí pokoje (28 m², 21 m² a 21 m²), které poskytují dostatek soukromí pro každého člena domácnosti. "
        + "Dům byl postaven jako zděný v roce 1986 a v roce 2019 prošel dílčí rekonstrukcí. Disponuje plastovými okny s izolačním dvojsklem, elektroinstalací v mědi a rozvody vody v plastu. "
        + "Pozemek o celkové výměře 722 m² (zastavěná plocha a nádvoří 207 m²) nabízí udržovanou zahradu s pergolou, vzrostlými ovocnými stromy i okrasnými dřevinami.";

    private static DuplicateCandidate Make(Guid source, string? description, double? builtUp, double? land,
        string? municipality, double? lat, double? lon, bool precise, string title, decimal price = 7_999_000m)
        => new(Guid.NewGuid(), source, PropertyType.House, OfferType.Sale, price, lat, lon, municipality, builtUp, land,
            new DateTime(2026, 9, 1), precise, "Znojmo", title, null, description);

    private static DuplicateCandidate SrealityHouse(string? description = null)
        => Make(Sreality, description ?? Intro + "\r\n\r\n" + Body.Replace(". ", ".\r\n"), 237, 929, "Prosiměřice",
            48.9027, 16.1920, true, "Prodej rodinného domu 237 m², pozemek 929 m²");

    private static DuplicateCandidate BazosHouse(string? description = null, decimal price = 7_999_000m)
        => Make(Bazos, description ?? Body, 207, 722, null, 48.8942, 16.1871, false,
            "Rodinný dům se zahradou, saunou a garáží – Prosiměřice", price);

    [Fact]
    public void SameDescription_ExactPrice_IsDuplicate_EvenWithConflictingAreas()
        => Assert.True(DuplicateDetectionService.IsDuplicatePair(SrealityHouse(), BazosHouse()));

    [Fact]
    public void SameDescription_DifferentPrice_NotDuplicate()
        => Assert.False(DuplicateDetectionService.IsDuplicatePair(SrealityHouse(), BazosHouse(price: 7_890_000m)));

    [Fact]
    public void DifferentDescription_ConflictingAreas_NotDuplicate()
    {
        var other = "Nabízíme k prodeji řadový dům v centru obce s menší zahradou a dvorem. "
            + "Dům prošel v roce 2015 kompletní rekonstrukcí včetně střechy, rozvodů a koupelny, vytápění je plynovým kotlem. "
            + "V přízemí je obývací pokoj s kuchyní, ložnice a koupelna, v podkroví dva pokoje a šatna. "
            + "K domu patří dvougaráž, kůlna a sklep. Obec má školu, školku, obchod i autobusové spojení do Znojma.";

        Assert.False(DuplicateDetectionService.IsDuplicatePair(SrealityHouse(), BazosHouse(other)));
    }

    [Fact]
    public void DescriptionsMatch_IgnoresLineBreaksPunctuationAndAddedIntro()
        => Assert.True(DuplicateDetectionService.DescriptionsMatch(Intro + "\r\n" + Body.ToUpperInvariant(), Body.Replace(" (", " [").Replace(")", "]")));

    [Fact]
    public void DescriptionsMatch_SharedTemplateWithDifferentHalf_NotMatch()
    {
        var firstHalf = Body[..(Body.Length / 2)];
        var other = firstHalf + " Byt se nachází ve třetím patře domu s výtahem, má lodžii orientovanou na východ, sklep a parkovací stání v podzemní garáži, "
            + "kuchyňská linka je nová se spotřebiči značky Bosch a v ceně je i vestavěná skříň v předsíni a v ložnici.";

        Assert.False(DuplicateDetectionService.DescriptionsMatch(Body, other));
    }

    [Fact]
    public void DescriptionsMatch_ShortText_NotEvidence()
        => Assert.False(DuplicateDetectionService.DescriptionsMatch("Prodej domu, volejte makléři.", "Prodej domu, volejte makléři."));

    [Fact]
    public void SameDescription_TwoUnitsInEachSource_Ambiguous_NoGroup()
    {
        // Dvě řadovky jednoho developera: stejný text i cena, jiné výměry
        var sreality1 = SrealityHouse(Body);
        var sreality2 = sreality1 with { Id = Guid.NewGuid(), AreaBuiltUp = 180, AreaLand = 410, Latitude = 48.9040, Title = "Prodej rodinného domu 180 m², pozemek 410 m²" };
        var bazos1 = BazosHouse(Body);
        var bazos2 = bazos1 with { Id = Guid.NewGuid(), AreaBuiltUp = 150, AreaLand = 380, Title = "Řadový dům Prosiměřice B" };

        Assert.Empty(DuplicateDetectionService.BuildClusters([sreality1, sreality2, bazos1, bazos2]));
    }

    // Horní Leska (6. 10. 2026): navštívený dům zmizel ze Sreality, ale na webu realitky běží dál.
    // Stažená kopie s uživatelovým záznamem se připojí k živé, aby stav a poznámky nezůstaly viset.

    [Fact]
    public void Remembered_GoneCopy_AttachesToLiveCopy()
    {
        var gone = SrealityHouse(Body) with { IsActive = false };
        var live = BazosHouse(Body);

        var attached = DuplicateDetectionService.AttachRemembered([live], [gone], new Dictionary<Guid, Guid>());

        Assert.Equal(live.Id, attached[gone.Id]);
    }

    [Fact]
    public void Remembered_LiveCopyInGroup_AttachesToGroupPrimary()
    {
        var gone = SrealityHouse(Body) with { IsActive = false };
        var live = BazosHouse(Body);
        var primaryId = Guid.NewGuid();

        var attached = DuplicateDetectionService.AttachRemembered(
            [live], [gone], new Dictionary<Guid, Guid> { [live.Id] = primaryId });

        Assert.Equal(primaryId, attached[gone.Id]);
    }

    [Fact]
    public void Remembered_RelistedInSameSource_Attaches()
    {
        var gone = SrealityHouse(Body) with { IsActive = false };
        var relisted = gone with { Id = Guid.NewGuid(), IsActive = true };

        var attached = DuplicateDetectionService.AttachRemembered([relisted], [gone], new Dictionary<Guid, Guid>());

        Assert.Equal(relisted.Id, attached[gone.Id]);
    }

    // Kubšice (21. 10. 2026): Sreality 5,7 mil. stažen 10. 10., 12. 10. tentýž dům znovu za 5,4 mil.
    // Opakování v jednom zdroji vyžaduje cenu na korunu, přeinzerování ale bývá právě se slevou –
    // stažený inzerát se proto páruje i volnějším pravidlem pro kopie (do 10 % se shodou všeho ostatního).

    [Fact]
    public void Remembered_RelistedInSameSourceWithLowerPrice_Attaches()
    {
        var gone = SrealityHouse(Body) with { IsActive = false, Price = 5_700_000m, District = "Znojmo", Disposition = "3+1" };
        var relisted = gone with { Id = Guid.NewGuid(), IsActive = true, Price = 5_400_000m };

        var attached = DuplicateDetectionService.AttachRemembered([relisted], [gone], new Dictionary<Guid, Guid>());

        Assert.Equal(relisted.Id, attached[gone.Id]);
    }

    [Fact]
    public void ActivePair_SameSourceDifferentPrice_StillNotDuplicate()
    {
        var a = SrealityHouse(Body) with { Price = 5_700_000m };
        var b = a with { Id = Guid.NewGuid(), Price = 5_400_000m };

        Assert.False(DuplicateDetectionService.IsDuplicatePair(a, b));
    }

    [Fact]
    public void Remembered_TwoDifferentLiveHouses_Ambiguous_NotAttached()
    {
        var gone = SrealityHouse(Body) with { IsActive = false };
        var live1 = BazosHouse(Body);
        var live2 = BazosHouse(Body) with { SourceId = Guid.NewGuid() };

        Assert.Empty(DuplicateDetectionService.AttachRemembered([live1, live2], [gone], new Dictionary<Guid, Guid>()));
    }

    [Fact]
    public void Remembered_DifferentHouse_NotAttached()
    {
        var gone = SrealityHouse(Body) with { IsActive = false };
        var other = BazosHouse("Úplně jiný dům v jiné ulici, po rekonstrukci, s garáží a bazénem.") with { Price = 4_500_000m };

        Assert.Empty(DuplicateDetectionService.AttachRemembered([other], [gone], new Dictionary<Guid, Guid>()));
    }
}

// ─────────────────────────────────────────────────────────────────
//  Kopie bez ceny (25. 10. 2026): Realcity má u 34 z 267 inzerátů „cena dohodou" a bez ceny se inzerát
//  do párování vůbec nedostal – Oslavany, Havířská 220 m² zůstala vedle Sreality/iDNES/Reas (7 999 999 Kč),
//  ačkoli popis je slovo od slova stejný.
// ─────────────────────────────────────────────────────────────────
public class DuplicateDetectionPricelessTests
{
    private static readonly Guid Realcity = Guid.NewGuid();
    private static readonly Guid Sreality = Guid.NewGuid();
    private static readonly Guid Idnes = Guid.NewGuid();

    private const string Text =
        "Nabízíme k prodeji rodinný dům v Oslavanech v ulici Havířská o užitné ploše 220 m² na pozemku 500 m². "
        + "Dům je dvoupodlažní, zděný, s valbovou střechou z roku 2008 a plastovými okny. V přízemí je obývací pokoj "
        + "s kuchyňským koutem a vstupem na terasu, pracovna, koupelna a technická místnost s plynovým kotlem. "
        + "V patře jsou tři ložnice, šatna a druhá koupelna s vanou. Na pozemku je garáž pro dvě auta, zahradní domek, "
        + "vzrostlé ovocné stromy a venkovní bazén s přístřeškem. Veškeré sítě jsou zavedeny, možnost ihned bydlet.";

    private const string OtherText =
        "Prodej řadového domu v Oslavanech po částečné rekonstrukci. V přízemí vstupní hala, kuchyně, obývací pokoj "
        + "a koupelna, v podkroví dvě ložnice a komora. Vytápění tuhými palivy, voda z obecního řadu, kanalizace do "
        + "septiku. Za domem úzký dvorek s kůlnou a malá zahrádka. Dům vyžaduje dokončení fasády a výměnu oken, "
        + "cena odpovídá stavu. Obec má kompletní občanskou vybavenost včetně školy, lékaře a vlakového spojení do Brna.";

    private static DuplicateCandidate Make(Guid source, decimal? price, string? description = Text,
        string municipality = "Oslavany", double? builtUp = 220, double? land = 500, string? disposition = null,
        double? lat = null, double? lon = null, bool precise = false, string district = "Brno-venkov",
        string title = "Prodej domu 220 m²")
        => new(Guid.NewGuid(), source, PropertyType.House, OfferType.Sale, price, lat, lon, municipality, builtUp, land,
            new DateTime(2026, 9, 30), precise, district, title, disposition, description);

    [Fact]
    public void PricelessCopy_SameDescriptionAndArea_SameMunicipality_IsDuplicate()
        => Assert.True(DuplicateDetectionService.IsDuplicatePair(
            Make(Realcity, price: null, land: null),
            Make(Sreality, price: 7_999_999m)));

    [Fact]
    public void PricelessCopy_DifferentDescription_NotDuplicate()
        => Assert.False(DuplicateDetectionService.IsDuplicatePair(
            Make(Realcity, price: null, description: OtherText),
            Make(Sreality, price: 7_999_999m)));

    [Fact]
    public void PricelessCopy_ConflictingArea_NotDuplicate()
        => Assert.False(DuplicateDetectionService.IsDuplicatePair(
            Make(Realcity, price: null, builtUp: 160),
            Make(Sreality, price: 7_999_999m)));

    [Fact]
    public void PricelessCopy_OtherMunicipalityWithoutGps_NotDuplicate()
        => Assert.False(DuplicateDetectionService.IsDuplicatePair(
            Make(Realcity, price: null, municipality: "Ivančice"),
            Make(Sreality, price: 7_999_999m)));

    [Fact]
    public void PricelessCopy_BothWithoutPrice_SameTextAndAreas_IsDuplicate()
        => Assert.True(DuplicateDetectionService.IsDuplicatePair(
            Make(Realcity, price: null),
            Make(Idnes, price: null)));

    [Fact]
    public void BuildClusters_PricelessJoinsPricedGroup()
    {
        var realcity = Make(Realcity, price: null);
        var sreality = Make(Sreality, price: 7_999_999m);
        var idnes = Make(Idnes, price: 7_999_999m, lat: 49.1255, lon: 16.3345);

        var map = DuplicateDetectionService.BuildClusters([realcity, sreality, idnes]);

        Assert.Equal(2, map.Count);
        Assert.Single(map.Values.Distinct());
        Assert.Contains(realcity.Id, map.Keys.Append(map.Values.First()));
    }

    [Fact]
    public void BuildClusters_PricelessWithTwoTemplateUnitsInOneSource_StaysAlone()
    {
        // Developer: dva byty se stejným textem i plochou na Sreality – bez ceny nelze říct, který to je
        var realcity = Make(Realcity, price: null, disposition: "2+kk");
        var unitA = Make(Sreality, price: 6_500_000m, disposition: "2+kk");
        var unitB = Make(Sreality, price: 6_600_000m, disposition: "2+kk");

        var map = DuplicateDetectionService.BuildClusters([realcity, unitA, unitB]);

        Assert.DoesNotContain(realcity.Id, map.Keys);
        Assert.DoesNotContain(realcity.Id, map.Values);
    }
}

// ─────────────────────────────────────────────────────────────────
//  Tasovice, Bílé domy (7. 11. 2026): jedna skupina duplicit míchala pět různých domů jednoho
//  developera – všechny na témže špendlíku 48,83610 N 16,15558 E (obec bez adresy), se stejnou
//  šablonou textu. Stažené domy se připojily k novému inzerátu přes „GPS do 300 m + cena ±2 %"
//  a detail ukazoval cizí fotky i historii ceny. Přeinzerování téhož domu (Kubšice) musí dál projít.
// ─────────────────────────────────────────────────────────────────
public class DuplicateDetectionDevelopmentTests
{
    private static readonly Guid Sreality = Guid.NewGuid();
    private static readonly Guid Bazos = Guid.NewGuid();
    private static readonly Guid Idnes = Guid.NewGuid();
    private static readonly Guid UlovDomov = Guid.NewGuid();

    private const double PinLat = 48.836095077282;
    private const double PinLon = 16.155584667625728;

    private const string TasoviceText =
        "Nabízíme k prodeji rodinný dům 4+kk v obci Tasovice. Tato nemovitost je ideální volbou pro ty, kteří si chtějí "
        + "dotvořit svůj domov podle vlastních představ, jelikož se jedná o hrubou stavbu. Stavba domu je cihlová, což zajišťuje "
        + "solidní a trvanlivou konstrukci. Dům je přízemní a nabízí prostorné 4 pokoje, které jsou dobrou volbou pro rodinu "
        + "hledající pohodlí a dostatek místa pro všechny členy. Celková užitná plocha domu činí 129 m², což poskytuje dostatek "
        + "prostoru pro vaše bydlení a případné úpravy interiéru. Pozemek má rozlohu 594 m² a poskytuje příjemný prostor pro "
        + "venkovní aktivity, relaxaci či zahradničení. Součástí domu je také garáž, která nabízí možnost bezpečného parkování "
        + "vašeho vozidla či úložné řešení pro vaše věci. Pokud Vás dům zaujal, neváhejte nás kontaktovat pro více informací.";

    private static DuplicateCandidate Make(Guid source, decimal? price, double? builtUp, double? land, string? disposition,
        DateTime firstSeen, DateTime lastSeen, bool active = true, double? lat = PinLat, double? lon = PinLon,
        bool precise = true, string? municipality = "Tasovice", string? description = null, string? title = null,
        PropertyType type = PropertyType.House, OfferType offer = OfferType.Sale)
        => new(Guid.NewGuid(), source, type, offer, price, lat, lon, municipality, builtUp, land, firstSeen, precise,
            "Znojmo", title ?? $"Prodej rodinného domu {builtUp} m², pozemek {land} m²", disposition, description, active, lastSeen);

    private static DateTime D(int month, int day) => new(2026, month, day, 2, 0, 0, DateTimeKind.Utc);

    private static Guid GroupOf(IReadOnlyDictionary<Guid, Guid> map, Guid id) => map.TryGetValue(id, out var primary) ? primary : id;

    // Skutečné řádky produkce (zkrácené na to, co párování čte)
    private static DuplicateCandidate CurrentRd03A() => Make(Sreality, 5_850_000m, 100, 594, "4+KK", D(11, 5), D(11, 6), description: TasoviceText);
    private static DuplicateCandidate Gone594() => Make(Sreality, 5_850_000m, 129, 594, "4+KK", D(2, 23), D(11, 2), active: false, description: TasoviceText);
    private static DuplicateCandidate Gone600() => Make(Sreality, 5_780_000m, 128, 600, "4+KK", D(2, 23), D(10, 21), active: false, description: TasoviceText);
    private static DuplicateCandidate Gone918() => Make(Sreality, 5_950_000m, 130, 918, "3+KK", D(2, 23), D(9, 29), active: false);
    private static DuplicateCandidate BazosRd3() => Make(Bazos, 5_850_000m, 129, null, "4+KK", D(9, 30), D(11, 6),
        lat: null, lon: null, municipality: null, description: TasoviceText, title: "RD 3");

    [Fact]
    public void Tasovice_FiveHousesOfOneDeveloper_CurrentListingStaysAlone()
    {
        var current = CurrentRd03A();
        var gone594 = Gone594();
        var gone600 = Gone600();
        var gone918 = Gone918();
        var bazos = BazosRd3();
        var otherHouse = Make(Sreality, 6_980_000m, 183, 790, "5+KK", D(11, 5), D(11, 6));

        var map = DuplicateDetectionService.ComputeMapping([current, gone594, gone600, gone918, bazos, otherHouse], out _);

        var group = GroupOf(map, current.Id);
        Assert.All([gone594, gone600, gone918, bazos], c => Assert.NotEqual(group, GroupOf(map, c.Id)));
    }

    [Fact]
    public void Tasovice_BazosCopyKeepsItsOwnHouse_The129m2One()
    {
        var gone594 = Gone594();
        var bazos = BazosRd3();

        var map = DuplicateDetectionService.ComputeMapping([CurrentRd03A(), gone594, Gone600(), Gone918(), bazos], out _);

        Assert.Equal(GroupOf(map, bazos.Id), GroupOf(map, gone594.Id));
    }

    [Fact]
    public void Kubsice_RelistedWithDiscountAndNewDisposition_AllCopiesInOneGroup()
    {
        // Sreality 5,7 mil. naposledy 8. 10., od 12. 10. znovu za 5,4 mil. jako 4+1; iDNES totéž
        var srealityGone = Make(Sreality, 5_700_000m, 200, 1330, "3+1", D(6, 15), D(10, 8), active: false,
            lat: 49.0001472222, lon: 16.4151186111, municipality: "Kubšice");
        var srealityLive = Make(Sreality, 5_400_000m, 200, 1330, "4+1", D(10, 12), D(11, 6),
            lat: 49.0001472222, lon: 16.4151186111, municipality: "Kubšice");
        var idnesGone = Make(Idnes, 5_700_000m, 200, 1330, "3+1", D(10, 6), D(10, 8), active: false,
            lat: null, lon: null, municipality: "Kubšice", title: "Prodej domu 200 m² s pozemkem 1 330 m²");
        var idnesLive = Make(Idnes, 5_400_000m, 200, 1330, "4+1", D(10, 12), D(11, 6),
            lat: 49.0006644, lon: 16.4130603, precise: false, municipality: "Kubšice", title: "Prodej domu 200 m² s pozemkem 1 330 m²");

        var map = DuplicateDetectionService.ComputeMapping([srealityGone, srealityLive, idnesGone, idnesLive], out var remembered);

        Assert.Equal(2, remembered);
        Assert.Single(new[] { srealityGone, srealityLive, idnesGone, idnesLive }.Select(c => GroupOf(map, c.Id)).Distinct());
    }

    // ── Přeinzerování v jednom zdroji ────────────────────────────────

    [Fact]
    public void Relisting_DifferentBuiltUpArea_NotSameHouse()
        => Assert.False(DuplicateDetectionService.IsSameSourceRelisting(Gone594() with { Price = 5_950_000m }, CurrentRd03A()));

    [Fact]
    public void Relisting_DifferentBothAreas_NotSameHouse()
        => Assert.False(DuplicateDetectionService.IsSameSourceRelisting(Gone600(), CurrentRd03A()));

    [Fact]
    public void Relisting_DifferentLandAndDisposition_NotSameHouse()
        => Assert.False(DuplicateDetectionService.IsSameSourceRelisting(Gone918(), CurrentRd03A()));

    [Fact]
    public void Relisting_RanSideBySideForMonths_NotSameHouse()
    {
        // Dva domy developera se stejnými plochami běžely vedle sebe – starý není přeinzerovaný nový
        var gone = Make(Sreality, 5_850_000m, 129, 594, "4+KK", D(2, 23), D(11, 2), active: false, lat: 48.9, lon: 16.2);
        var live = Make(Sreality, 5_780_000m, 129, 594, "4+KK", D(3, 1), D(11, 6), lat: 48.9, lon: 16.2);

        Assert.False(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    [Fact]
    public void Relisting_ShortOverlapWithDiscount_SameHouse()
    {
        // Kratochvilka: nový inzerát za 3,39 mil. od 5. 10., starý za 3,68 mil. visel do 12. 10.
        var gone = Make(Sreality, 3_680_000m, 55, 204, "1+1", D(9, 24), D(10, 12), active: false, lat: 48.95, lon: 16.4, municipality: "Kratochvilka");
        var live = Make(Sreality, 3_390_000m, 55, 204, "1+1", D(10, 5), D(11, 6), lat: 48.95, lon: 16.4, municipality: "Kratochvilka");

        Assert.True(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    [Fact]
    public void Relisting_OtherDispositionWithoutBothAreas_NotSameHouse()
    {
        var gone = Make(Sreality, 5_900_000m, 100, null, "3+KK", D(8, 1), D(9, 1), active: false, lat: 48.9, lon: 16.2, type: PropertyType.Apartment);
        var live = Make(Sreality, 5_800_000m, 100, null, "4+KK", D(9, 3), D(11, 6), lat: 48.9, lon: 16.2, type: PropertyType.Apartment);

        Assert.False(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    [Fact]
    public void Relisting_BazosRepostWithSameTitle_SameHouse()
    {
        // Bazoš nemá obec ani přesnou GPS – znovu vložený inzerát pozná podle titulku
        var gone = Make(Bazos, 401_236m, null, 659_064, null, D(10, 6), D(11, 6), active: false, lat: null, lon: null,
            municipality: null, title: "Les 659 064 m², Podíl 4 % o výměře 29 065 m²", type: PropertyType.Land);
        var live = gone with { Id = Guid.NewGuid(), FirstSeenAt = D(10, 16), IsActive = true };

        Assert.True(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    [Fact]
    public void Relisting_CorrectedTypoInArea_SameHouse()
    {
        // Pozořice: „289 m², pozemek 289 m²" do 15. 9., od 16. 9. „210 m², pozemek 289 m²", jinak vše stejné
        var gone = Make(Sreality, 5_990_000m, 289, 289, "3+1", D(4, 27), D(9, 15), active: false, lat: 49.2103625, lon: 16.7935396, municipality: "Pozořice");
        var live = Make(Sreality, 5_990_000m, 210, 289, "3+1", D(9, 16), D(11, 6), lat: 49.2103625, lon: 16.7935396, municipality: "Pozořice");

        Assert.True(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    [Fact]
    public void Relisting_CorrectedAreaOnDeveloperPin_NotSameHouse()
    {
        // Tasovice vypadají stejně (129 → 100 m², pozemek i cena stejné, 3 dny), ale leží na špendlíku developera
        var gone = Gone594() with { SharedGps = true, TemplateDescription = true };
        var live = CurrentRd03A() with { SharedGps = true, TemplateDescription = true };

        Assert.False(DuplicateDetectionService.IsSameSourceRelisting(gone, live));
    }

    // ── Sdílený špendlík ─────────────────────────────────────────────

    [Fact]
    public void FindSharedPins_TwoUnitsSideBySide_MarksEveryListingOnThePoint()
    {
        var unitA = Gone594();
        var unitB = Gone600();
        var newcomer = CurrentRd03A();
        var otherPortal = Make(Idnes, 5_850_000m, 129, 594, "4+KK", D(10, 1), D(11, 6), lat: PinLat + 0.00002, lon: PinLon);
        var house300mAway = Make(Sreality, 4_000_000m, 90, 500, "3+1", D(3, 1), D(11, 6), lat: PinLat + 0.0027, lon: PinLon);

        var pins = DuplicateDetectionService.FindSharedPins([unitA, unitB, newcomer, otherPortal, house300mAway]);

        Assert.Contains(unitA.Id, pins);
        Assert.Contains(unitB.Id, pins);
        Assert.Contains(newcomer.Id, pins);
        Assert.Contains(otherPortal.Id, pins);
        Assert.DoesNotContain(house300mAway.Id, pins);
    }

    [Fact]
    public void FindSharedPins_SameHouseListedTwice_NotPin()
    {
        var first = Make(Sreality, 7_490_000m, 314, 673, "5+1", D(2, 23), D(11, 6), lat: 48.8555, lon: 16.0488);
        var second = first with { Id = Guid.NewGuid(), FirstSeenAt = D(4, 1) };

        Assert.Empty(DuplicateDetectionService.FindSharedPins([first, second]));
    }

    [Fact]
    public void FindSharedPins_RelistingWithShortOverlap_NotPin()
    {
        var old = Make(Sreality, 3_680_000m, 55, 204, "1+1", D(9, 24), D(10, 12), active: false, lat: 48.95, lon: 16.4);
        var relisted = Make(Sreality, 3_390_000m, 55, 204, "1+1", D(10, 5), D(11, 6), lat: 48.95, lon: 16.4);

        Assert.Empty(DuplicateDetectionService.FindSharedPins([old, relisted]));
    }

    [Fact]
    public void SharedPin_ConflictingAreas_NotDuplicate()
    {
        var sreality = CurrentRd03A() with { SharedGps = true };
        var idnes = Make(Idnes, 5_900_000m, 129, 600, "4+KK", D(10, 1), D(11, 6), lat: PinLat + 0.0005, lon: PinLon) with { SharedGps = true };

        Assert.False(DuplicateDetectionService.IsDuplicatePair(sreality, idnes));
        // Bez špendlíku platí přesná GPS do 300 m jako dřív
        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality with { SharedGps = false }, idnes with { SharedGps = false }));
    }

    [Fact]
    public void SharedPin_MatchingAreas_StillDuplicate()
    {
        var sreality = Gone594() with { IsActive = true, SharedGps = true };
        var idnes = Make(Idnes, 5_900_000m, 129, 594, "4+KK", D(10, 1), D(11, 6), lat: PinLat + 0.0005, lon: PinLon);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, idnes));
    }

    [Fact]
    public void SharedPin_TwoKilometresApart_StillRejected()
    {
        var sreality = Gone594() with { IsActive = true, SharedGps = true };
        var idnes = Make(Idnes, 5_850_000m, 129, 594, "4+KK", D(10, 1), D(11, 6), lat: PinLat + 0.018, lon: PinLon);

        Assert.False(DuplicateDetectionService.IsDuplicatePair(sreality, idnes));
    }

    [Fact]
    public void SharedPin_RepeatNeedsAreasToTheMeter()
    {
        // Sousední domy developera se stejnou cenou: pozemek 594 vs. 600 m² není opakování téhož inzerátu
        var a = Make(Sreality, 8_850_000m, 128, 594, "4+KK", D(2, 23), D(11, 5), title: "Prodej rodinného domu 128 m², pozemek 594 m²");
        var b = Make(Sreality, 8_850_000m, 128, 600, "4+KK", D(2, 23), D(10, 22), title: "Prodej rodinného domu 128 m², pozemek 600 m²");

        Assert.True(DuplicateDetectionService.IsSameSourceRepeat(a, b));
        Assert.False(DuplicateDetectionService.IsSameSourceRepeat(a with { SharedGps = true }, b with { SharedGps = true }));
    }

    // ── Šablona textu developera ─────────────────────────────────────

    [Fact]
    public void FindTemplateDescriptions_TextOfUnitsSideBySide_MarksWholeText()
    {
        var unitA = Gone594();
        var unitB = Gone600();
        var newcomer = CurrentRd03A();
        var bazos = BazosRd3();

        var templates = DuplicateDetectionService.FindTemplateDescriptions([unitA, unitB, newcomer, bazos]);

        Assert.Contains(unitA.Id, templates);
        Assert.Contains(unitB.Id, templates);
        Assert.Contains(newcomer.Id, templates);
        Assert.DoesNotContain(bazos.Id, templates);  // Bazoš ho má jen jednou
    }

    [Fact]
    public void FindTemplateDescriptions_RepostOfSameHouse_NotTemplate()
    {
        var first = Gone594();
        var repost = first with { Id = Guid.NewGuid(), FirstSeenAt = D(5, 1), IsActive = true, LastSeenAt = D(11, 6) };

        Assert.Empty(DuplicateDetectionService.FindTemplateDescriptions([first, repost]));
    }

    [Fact]
    public void TemplateDescription_ConflictingArea_NotDuplicate()
        => Assert.False(DuplicateDetectionService.IsDuplicatePair(
            BazosRd3() with { TemplateDescription = true },
            CurrentRd03A() with { TemplateDescription = true, SharedGps = true }));

    [Fact]
    public void TemplateDescription_MatchingArea_StillDuplicate()
        => Assert.True(DuplicateDetectionService.IsDuplicatePair(
            BazosRd3() with { TemplateDescription = true },
            Gone594() with { IsActive = true, TemplateDescription = true, SharedGps = true }));

    [Fact]
    public void NonTemplateDescription_ConflictingArea_StillDuplicate()
        => Assert.True(DuplicateDetectionService.IsDuplicatePair(BazosRd3(), CurrentRd03A()));

    [Fact]
    public void TemplateDescription_UnroundExactPrice_StillDuplicate()
    {
        // Byt developera: cena spočítaná z metrů (8 870 803 Kč) je otisk jednotky, plochu RealityMIX počítá s terasou
        var sreality = Make(Sreality, 8_870_803m, 103, null, "3+KK", D(9, 25), D(11, 6), municipality: "Znojmo",
            description: TasoviceText, type: PropertyType.Apartment) with { TemplateDescription = true };
        var realityMix = Make(Bazos, 8_870_803m, 94.3, null, "3+kk", D(9, 30), D(11, 6), lat: null, lon: null,
            municipality: "Znojmo", description: TasoviceText, title: "Prodej moderního bytu s terasou", type: PropertyType.Apartment)
            with { TemplateDescription = true };

        Assert.True(DuplicateDetectionService.IsDuplicatePair(sreality, realityMix));
    }

    // ── Šablona titulku (Těšetice: Century 21 vede pod stejným titulkem domy s pozemkem 367 i 395 m²) ──

    [Fact]
    public void TemplateTitle_SameSourceUnitsWithDifferentLand_NotRepeat()
    {
        const string title = "Patrová novostavba 4+kk 125 m², pozemek 395 m², Těšetice u Znojma";
        var unit367 = Make(Bazos, 7_450_000m, 124, 367, "4+KK", D(9, 9), D(11, 6), lat: 48.8888441, lon: 16.1582019,
            precise: false, municipality: "Těšetice", title: title);
        var unit395 = Make(Bazos, 7_450_000m, 124, 395, "4+KK", D(9, 12), D(11, 6), lat: 48.8888441, lon: 16.1582019,
            precise: false, municipality: "Těšetice", title: title);

        var templates = DuplicateDetectionService.FindTemplateTitles([unit367, unit395]);

        Assert.Equal(2, templates.Count);
        Assert.False(DuplicateDetectionService.IsSameSourceRepeat(
            unit367 with { TemplateTitle = true }, unit395 with { TemplateTitle = true }));
    }

    [Fact]
    public void TemplateTitle_SrealityAutoTitleWithSameAreas_StillRepeat()
    {
        // Automatický titulek Sreality sdílí víc domů, opakování téhož domu (stejné plochy) ale projde
        var a = Make(Sreality, 7_490_000m, 314, 673, "5+1", D(2, 23), D(11, 6), lat: 48.8555, lon: 16.0488) with { TemplateTitle = true };
        var b = a with { Id = Guid.NewGuid(), Latitude = 48.8600, FirstSeenAt = D(5, 1) };

        Assert.True(DuplicateDetectionService.IsSameSourceRepeat(a, b));
    }

    // ── Stažené inzeráty ve skupině ──────────────────────────────────

    [Fact]
    public void Remembered_TwoUnitsThatRanSideBySide_NeitherAttached()
    {
        // Oba stažené domy sedí na živou kopii, ale běžely vedle sebe – nevíme, který z nich to je
        var live = Make(Idnes, 5_850_000m, 129, null, "4+KK", D(10, 1), D(11, 6), lat: null, lon: null);
        var unitA = Make(Sreality, 5_850_000m, 129, 594, "4+KK", D(2, 23), D(11, 2), active: false, lat: 48.9, lon: 16.2);
        var unitB = Make(Sreality, 5_850_000m, 129, 700, "4+KK", D(2, 23), D(10, 21), active: false, lat: 48.9, lon: 16.21);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(unitA, live));
        Assert.True(DuplicateDetectionService.IsDuplicatePair(unitB, live));
        Assert.Empty(DuplicateDetectionService.AttachRemembered([live], [unitA, unitB], new Dictionary<Guid, Guid>()));
    }

    [Fact]
    public void Remembered_GoneCopyContradictingLiveListing_NotAttached()
    {
        // Pronájem 22 500 Kč do 300 m: stažený byt 40 m² není živý byt 55 m²
        var live = Make(UlovDomov, 22_500m, 55, null, "2+KK", D(9, 30), D(11, 6), lat: 49.2, lon: 16.6,
            municipality: "Brno", type: PropertyType.Apartment, offer: OfferType.Rent);
        var gone = Make(Sreality, 22_900m, 40, null, "2+KK", D(9, 25), D(10, 26), active: false, lat: 49.2005, lon: 16.6,
            municipality: "Brno", type: PropertyType.Apartment, offer: OfferType.Rent);

        Assert.True(DuplicateDetectionService.IsDuplicatePair(gone, live));
        Assert.Empty(DuplicateDetectionService.AttachRemembered([live], [gone], new Dictionary<Guid, Guid>()));
    }

    [Fact]
    public void Remembered_SameSourceOnlyThroughRelistingRules()
    {
        // Dřív se stažený inzerát téhož zdroje pároval i volnými pravidly pro cizí portály (GPS do 300 m + cena ±2 %)
        var live = CurrentRd03A();
        var gone = Gone918();

        Assert.Empty(DuplicateDetectionService.AttachRemembered([live], [gone], new Dictionary<Guid, Guid>()));
    }
}
