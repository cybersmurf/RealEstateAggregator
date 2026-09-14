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
        double lat = 48.8555, double lon = 16.0488)
        => new(
            Guid.NewGuid(), source, PropertyType.House, OfferType.Sale,
            price, lat, lon, "Znojmo", 314, 673,
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
        var bazos = Enumerable.Range(0, 3).Select(i => Make(SourceA, daysOld: 10 + i)).ToList();
        var sreality = Enumerable.Range(0, 3).Select(i => Make(SourceB, daysOld: i)).ToList();

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
            Make(SourceA, daysOld: 9, price: 2_160_000m, lat: 48.7550, lon: 16.2280),
            Make(SourceA, daysOld: 8, price: 2_160_000m, lat: 48.7550, lon: 16.2280),
            Make(SourceB, daysOld: 7, price: 2_160_000m, lat: 48.7550, lon: 16.2280),
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
}
