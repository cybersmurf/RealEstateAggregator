using RealEstate.Api.Services;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// Poloha domu vůči sousedům: samostatný / přisazený z jedné strany / řadový / rohový – určuje se z venkovních fotek.
public class HousePositionTests
{
    private static ListingPhoto Photo(int order, string? category, string? description = null, string? labels = null)
        => new() { Id = Guid.NewGuid(), Order = order, PhotoCategory = category, PhotoDescription = description, PhotoLabels = labels };

    [Fact]
    public void SelectPhotos_AerialFirst_ThenExterior_ThenLand()
    {
        var photos = new[]
        {
            Photo(0, "living_room", "Obývací pokoj s krbovými kamny."),
            Photo(1, "land", "Zahrada s ovocnými stromy."),
            Photo(2, "exterior", "Pohled na rodinný dům z ulice."),
            Photo(3, "exterior", "Letecký pohled na rodinný dům s uzavřeným dvorem."),
            Photo(4, "land", "Snímek z dronu: pozemek a okolní zástavba."),
        };

        var selected = HousePositionService.SelectPhotos(photos, 6);

        Assert.Equal([3, 4, 2, 1], selected.Select(p => p.Order));
    }

    [Fact]
    public void SelectPhotos_SkipsVisualizations()
    {
        var photos = new[]
        {
            Photo(0, "exterior", "Vizualizace: Pohled na dvůr s upravenou zahradou.", """["visualization","garden"]"""),
            Photo(1, "exterior", "Pohled na vnitřní dvůr rodinného domu."),
        };

        Assert.Equal([1], HousePositionService.SelectPhotos(photos, 6).Select(p => p.Order));
    }

    [Fact]
    public void SelectPhotos_RespectsLimit_AndIgnoresUnclassified()
    {
        var photos = Enumerable.Range(0, 10).Select(i => Photo(i, "exterior")).Append(Photo(10, null)).ToList();

        Assert.Equal(HousePositionService.MaxPhotos, HousePositionService.SelectPhotos(photos, HousePositionService.MaxPhotos).Count);
    }

    [Fact]
    public void SelectPhotos_NoExterior_IsEmpty()
        => Assert.Empty(HousePositionService.SelectPhotos([Photo(0, "kitchen"), Photo(1, "floor_plan")], 6));

    [Theory]
    [InlineData("detached", "detached")]
    [InlineData("Semi-Detached", "semi_detached")]
    [InlineData("semi detached", "semi_detached")]
    [InlineData("TERRACED", "terraced")]
    [InlineData("corner", "corner")]
    [InlineData("unknown", "unknown")]
    public void ParseVerdict_NormalizesPosition(string raw, string expected)
    {
        var verdict = HousePositionService.ParseVerdict($$"""{"position":"{{raw}}","reason":"Vlevo vjezd, vpravo sousedova stodola."}""");

        Assert.NotNull(verdict);
        Assert.Equal(expected, verdict.Position);
        Assert.Equal("Vlevo vjezd, vpravo sousedova stodola.", verdict.Reason);
    }

    [Fact]
    public void ParseVerdict_ReadsMapMatchFlag()
    {
        Assert.True(HousePositionService.ParseVerdict("""{"position":"detached","map_matches_photos":true,"reason":"x"}""")!.MapMatchesPhotos);
        Assert.False(HousePositionService.ParseVerdict("""{"position":"detached","map_matches_photos":false,"reason":"x"}""")!.MapMatchesPhotos);
        Assert.Null(HousePositionService.ParseVerdict("""{"position":"detached","map_matches_photos":null,"reason":"x"}""")!.MapMatchesPhotos);
        Assert.Null(HousePositionService.ParseVerdict("""{"position":"detached","reason":"x"}""")!.MapMatchesPhotos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("není JSON")]
    [InlineData("""{"position":"row_house","reason":"neznámá hodnota"}""")]
    [InlineData("""{"position":3}""")]
    [InlineData("""["detached"]""")]
    public void ParseVerdict_UnusableAnswer_IsNull(string? raw)
        => Assert.Null(HousePositionService.ParseVerdict(raw));

    [Fact]
    public void WithEvidenceNote_NoAerialPhoto_SaysSoInReason()
    {
        Assert.Equal("Vlevo vjezd.", HousePositionService.WithEvidenceNote("Vlevo vjezd.", hasAerial: true));
        Assert.Equal($"{HousePositionService.NoAerialNote} Vlevo vjezd.", HousePositionService.WithEvidenceNote("Vlevo vjezd.", hasAerial: false));
        Assert.Equal(HousePositionService.NoAerialNote, HousePositionService.WithEvidenceNote("", hasAerial: false));
    }

    [Theory]
    [InlineData("Letecký pohled na rodinný dům s uzavřeným dvorem.", true)]
    [InlineData("Snímek z dronu: pozemek a okolní zástavba.", true)]
    [InlineData("Pohled na rodinný dům z ulice.", false)]
    [InlineData(null, false)]
    public void IsAerial_FollowsClassifierDescription(string? description, bool expected)
        => Assert.Equal(expected, HousePositionService.IsAerial(Photo(0, "exterior", description)));

    [Theory]
    [InlineData("detached", "samostatný")]
    [InlineData("semi_detached", "přisazený z jedné strany")]
    [InlineData("terraced", "řadový")]
    [InlineData("corner", "rohový")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Label_IsCzech_AndNullWhenUndetermined(string? position, string? expected)
        => Assert.Equal(expected, HousePositions.Label(position));

    // ── Sladění katastru, makléře a obrazového modelu (kontrola 47 domů 7. 11. 2026) ──

    private static CadastreVerdict? Cadastre(string? position)
        => position is null ? null : new CadastreVerdict(position, $"Katastr: {position}.", "č.p. 1");

    private static HousePositionVerdict? Model(string? position)
        => position is null ? null : new HousePositionVerdict(position, "Model: na všech stranách je mezera.");

    [Theory]
    [InlineData("Samostatný", "detached")]
    [InlineData("Řadový", "terraced")]
    [InlineData("Rohový", "corner")]
    [InlineData("V bloku", "terraced")]
    [InlineData("Dvojdům", "semi_detached")]
    [InlineData("  řadový ", "terraced")]
    [InlineData("Jiný", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BrokerPosition_MapsSrealityValues(string? listed, string? expected)
        => Assert.Equal(expected, HousePositionService.BrokerPosition(listed));

    [Theory]
    // Kubšice / Únanov / Břežany: model „detached" bez katastru nepřebije makléřovo „Řadový"
    [InlineData(null, "Řadový", "detached", "terraced", "broker")]
    // Tvořihráz: novostavba není v katastru, jen vizualizace – „V bloku" platí
    [InlineData(null, "V bloku", "detached", "terraced", "broker")]
    // Oleksovice: „Rohový" = přistavěný z jedné strany
    [InlineData(null, "Rohový", "detached", "corner", "broker")]
    // Čejkovice: GPS na obecní budově, makléř „Rohový", model „semi" – typ od makléře (konec řady)
    [InlineData(null, "Rohový", "semi_detached", "corner", "broker")]
    // Štítary / Černín: k sousedovi přiléhá jen vedlejší stavba – makléřovo „Samostatný" platí
    [InlineData(null, "Samostatný", "semi_detached", "detached", "broker")]
    // Suchohrdly: katastr změřil společné zdi z obou stran – typ dodá katastr, model se mýlil
    [InlineData("terraced", "Rohový", "detached", "terraced", "cadastre")]
    // Drnholec: shoda na „přistavěný", typ z katastru (konec řady), ne „semi" od modelu
    [InlineData("corner", "Řadový", "semi_detached", "corner", "cadastre")]
    // Damnice: katastr i snímky ukazují samostatný dům, makléř „Řadový" – 2 : 1
    [InlineData("detached", "Řadový", "detached", "detached", "cadastre")]
    // Katastr sám makléřovo „Řadový" nepřebije (remíza 1 : 1, model nerozhodl)
    [InlineData("detached", "Řadový", "unknown", "terraced", "broker")]
    // Hostim: v katastru chybí přístavby, snímky je vidí – bez makléře remíza → přistavěný
    [InlineData("detached", null, "terraced", "terraced", "vision")]
    // Horní Věstonice: katastr společnou zeď, makléř „Samostatný", model nic – remíza → přistavěný
    [InlineData("corner", "Samostatný", null, "corner", "cadastre")]
    // Černín: špendlík na cizí řadě, makléř i snímky samostatný – 2 : 1 proti katastru
    [InlineData("corner", "Samostatný", "detached", "detached", "broker")]
    // Katastr i model přistavěný proti makléřovu „Samostatný"
    [InlineData("semi_detached", "Samostatný", "semi_detached", "semi_detached", "cadastre")]
    // Model „detached" proti společné zdi v katastru (jeho typická chyba)
    [InlineData("semi_detached", null, "detached", "semi_detached", "cadastre")]
    [InlineData("detached", "Samostatný", "detached", "detached", "cadastre")]
    [InlineData(null, null, "detached", "detached", "vision")]
    [InlineData(null, null, "terraced", "terraced", "vision")]
    [InlineData(null, null, "unknown", "unknown", "vision")]
    [InlineData(null, "Řadový", "unknown", "terraced", "broker")]
    [InlineData("detached", null, null, "detached", "cadastre")]
    public void Reconcile_TwoOfThreeDecideDetached(string? cadastre, string? broker, string? model, string expected, string source)
    {
        var decision = HousePositionService.Reconcile(Cadastre(cadastre), broker, Model(model));

        Assert.NotNull(decision);
        Assert.Equal(expected, decision.Position);
        Assert.Equal(source, decision.Source);
    }

    [Fact]
    public void Reconcile_NothingNew_IsNull()
    {
        Assert.Null(HousePositionService.Reconcile(null, "Řadový", null));
        Assert.Null(HousePositionService.Reconcile(new CadastreVerdict(null, "Katastr: GPS neleží na obytné budově."), "Řadový", null));
    }

    [Fact]
    public void Reconcile_InconclusiveCadastre_DoesNotVote()
    {
        var decision = HousePositionService.Reconcile(new CadastreVerdict(null, "Katastr: GPS neleží na obytné budově."), "Řadový", Model("detached"));

        Assert.Equal(HousePositions.Terraced, decision!.Position);
        Assert.Equal(HousePositionSources.Broker, decision.Source);
        Assert.Contains("GPS neleží na obytné budově", decision.Reason);
    }

    [Fact]
    public void Reconcile_Conflict_ReasonSaysWhoSaidWhatAndWhy()
    {
        var reason = HousePositionService.Reconcile(Cadastre("detached"), "Řadový", Model("detached"))!.Reason;

        Assert.StartsWith("Rozpor – samostatný: katastr a snímky; přistavěný: makléř („Řadový“). Platí samostatný (shoda dvou zdrojů ze tří).", reason);
        Assert.Contains("Katastr: detached.", reason);
        Assert.Contains("Model: na všech stranách je mezera.", reason);
    }

    [Fact]
    public void Reconcile_BrokerBeatsModelWithoutCadastre_SaysSo()
    {
        var reason = HousePositionService.Reconcile(null, "Řadový", Model("detached"))!.Reason;

        Assert.Contains("Platí řadový (bez katastru rozhoduje makléř)", reason);
    }

    [Fact]
    public void Reconcile_Agreement_HasNoConflictNote()
    {
        var reason = HousePositionService.Reconcile(Cadastre("terraced"), "Řadový", Model("semi_detached"))!.Reason;

        Assert.DoesNotContain("Rozpor", reason);
        Assert.StartsWith("Katastr: terraced.", reason);
    }

    [Fact]
    public void Reconcile_ModelUnknown_BrokerDecides_SaysPhotosCouldNotTell()
    {
        var decision = HousePositionService.Reconcile(null, "Rohový", new HousePositionVerdict("unknown", "Boky domu nejsou vidět."));

        Assert.Equal(HousePositions.Corner, decision!.Position);
        Assert.StartsWith("Ze snímků polohu určit nejde – platí údaj makléře („Rohový“).", decision.Reason);
    }

    [Theory]
    [InlineData("detached", "Řadový", true)]
    [InlineData("detached", "V bloku", true)]
    [InlineData("semi_detached", "Samostatný", true)]
    [InlineData("corner", "Řadový", false)]
    [InlineData("detached", "Samostatný", false)]
    [InlineData("unknown", "Řadový", false)]
    [InlineData("detached", null, false)]
    [InlineData(null, "Řadový", false)]
    public void ContradictsBroker_OnlyDetachedVersusAttached(string? position, string? listed, bool expected)
        => Assert.Equal(expected, HousePositionService.ContradictsBroker(position, listed));

    [Fact]
    public void SelectForRedetection_ReadsBrokerClaimFromAnyCopyOfTheHouse()
    {
        var kubsice = Guid.NewGuid();
        var agreed = Guid.NewGuid();
        var byCadastre = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        HousePositionGroupRow[] rows =
        [
            // Sreality kopie nese údaj makléře, poloha je zapsaná u všech kopií
            new(kubsice, true, "detached", null, "Řadový"),
            new(kubsice, true, "detached", null, null),
            new(agreed, true, "terraced", HousePositionSources.Cadastre, "Řadový"),
            // Rozpor rozhodnutý katastrem se snímky je záměr – znovu se nespouští
            new(byCadastre, true, "detached", HousePositionSources.Cadastre, "Řadový"),
            new(legacy, true, "detached", null, null),
        ];

        Assert.Equal([kubsice], HousePositionService.SelectForRedetection(rows, HousePositionService.ScopeContradicting, activeOnly: true, limit: 100));

        var all = HousePositionService.SelectForRedetection(rows, HousePositionService.ScopeUnreconciled, activeOnly: true, limit: 100);
        Assert.Equal(kubsice, all[0]);
        Assert.Equal(new[] { kubsice, legacy }.Order(), all.Order());
    }

    [Fact]
    public void SelectForRedetection_ActiveOnlyAndLimit()
    {
        var gone = Guid.NewGuid();
        HousePositionGroupRow[] rows =
        [
            new(gone, false, "detached", null, "Řadový"),
            new(Guid.NewGuid(), true, "detached", null, "Rohový"),
            new(Guid.NewGuid(), true, "semi_detached", null, "Samostatný"),
        ];

        Assert.DoesNotContain(gone, HousePositionService.SelectForRedetection(rows, HousePositionService.ScopeContradicting, activeOnly: true, limit: 100));
        Assert.Equal(3, HousePositionService.SelectForRedetection(rows, HousePositionService.ScopeContradicting, activeOnly: false, limit: 100).Count);
        Assert.Single(HousePositionService.SelectForRedetection(rows, HousePositionService.ScopeContradicting, activeOnly: false, limit: 1));
    }

    [Fact]
    public void BuildPrompt_PassesBrokerClaimAndCadastreFacts()
    {
        var facts = new CadastreVerdict(HousePositions.Terraced, "Katastr: …", "č.p. 380",
            [new AttachedBuilding("č.p. 379", "IndividualResidence", 10), new AttachedBuilding("č.p. 381", "IndividualResidence", 11.6)], []).PromptFacts();

        var prompt = HousePositionService.BuildPrompt(hasCadastre: true, hasOrthophoto: true, galleryCount: 2, brokerListed: "Rohový", cadastreFacts: facts);

        Assert.Contains("The broker states the house position as \"Rohový\" (attached on one side - end of a row)", prompt);
        Assert.Contains("the building under the crosshair is č.p. 380", prompt);
        Assert.Contains("č.p. 381 (12 m of shared wall)", prompt);
        Assert.Contains("a shared edge is a common wall, not a gap", prompt);
        Assert.DoesNotContain("{EVIDENCE}", prompt);
    }

    [Fact]
    public void BuildPrompt_WithoutBrokerOrCadastre_HasNoEvidenceLines()
    {
        var prompt = HousePositionService.BuildPrompt(hasCadastre: false, hasOrthophoto: false, galleryCount: 3);

        Assert.DoesNotContain("broker", prompt);
        Assert.DoesNotContain("Cadastre (", prompt);
        Assert.DoesNotContain("{EVIDENCE}", prompt);
    }
}
