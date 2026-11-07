using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Services.Duplicates;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>Hodnoty <c>listings.house_position</c> – poloha domu vůči sousedním stavbám.</summary>
public static class HousePositions
{
    public const string Detached = "detached";
    public const string SemiDetached = "semi_detached";
    public const string Terraced = "terraced";
    public const string Corner = "corner";
    public const string Unknown = "unknown";

    public static readonly string[] All = [Detached, SemiDetached, Terraced, Corner, Unknown];

    /// <summary>Český název pro UI a MCP; null u neznámé nebo neurčené polohy.</summary>
    public static string? Label(string? position) => position switch
    {
        Detached => "samostatný",
        SemiDetached => "přisazený z jedné strany",
        Terraced => "řadový",
        Corner => "rohový",
        _ => null,
    };
}

/// <summary>Hodnoty <c>listings.house_position_source</c> – který zdroj o poloze domu rozhodl.</summary>
public static class HousePositionSources
{
    /// <summary>Půdorysy v katastru, potvrzené makléřem nebo snímky. Jen takový výsledek smí přebít makléřovo „Řadový".</summary>
    public const string Cadastre = "cadastre";
    /// <summary>Údaj makléře (Sreality „Poloha domu"), katastr ho nevyvrátil.</summary>
    public const string Broker = "broker";
    /// <summary>Jen obrazový model – bez katastru a bez údaje makléře.</summary>
    public const string Vision = "vision";
}

/// <summary><see cref="MapMatchesPhotos"/>: model potvrdil, že budova pod zaměřovačem je dům z fotek (null = mapu neměl nebo neřekl).</summary>
public sealed record HousePositionVerdict(string Position, string Reason, bool? MapMatchesPhotos = null);

/// <summary>Výsledné sladění katastru, makléře a obrazového modelu – to se ukládá.</summary>
public sealed record HousePositionDecision(string Position, string Reason, string Source);

public sealed record HousePositionResultDto(Guid ListingId, string? Position, string? Label, string? Reason, int PhotosUsed, string? Message, string? Source = null);

/// <summary>Dům (kořen skupiny duplicit) pro výběr k novému určení polohy.</summary>
public sealed record HousePositionGroupRow(Guid RootId, bool IsActive, string? Position, string? Source, string? Listed);

public sealed record HousePositionRedetectItemDto(Guid ListingId, string? Before, string? After, string? Source, string? Message);

public sealed record HousePositionRedetectResultDto(int Processed, int Changed, int Failed, List<HousePositionRedetectItemDto> Items);

public interface IHousePositionService
{
    /// <summary>Určí polohu domu z katastru, venkovních a leteckých fotek a údaje makléře a uloží ji ke všem kopiím inzerátu.</summary>
    Task<HousePositionResultDto> DetectAsync(Guid listingId, CancellationToken ct);

    /// <summary>
    /// Domy k novému určení: <c>contradicting</c> = uložená poloha odporuje makléři (samostatný × přistavěný)
    /// a nerozhodl katastr; <c>unreconciled</c> = navíc všechny určené před sladěním (source NULL).
    /// </summary>
    Task<List<Guid>> FindForRedetectionAsync(string scope, bool activeOnly, int limit, CancellationToken ct);

    /// <summary>Postupně znovu určí polohu u daných domů (jeden po druhém – šetří obrazový model i ČÚZK).</summary>
    Task<HousePositionRedetectResultDto> RedetectAsync(IReadOnlyList<Guid> listingIds, CancellationToken ct);
}

/// <summary>
/// Poloha domu: samostatný / přisazený z jedné strany / řadový / rohový. Tři zdroje, každý s jinou chybou:
/// <list type="bullet">
/// <item>Katastr (<see cref="HouseAdjacency"/>) – přesné půdorysy, ale špendlík inzerátu může stát na cizí
/// budově (Černín) a nezapsané přístavby chybí (Hostim).</item>
/// <item>Makléř (Sreality „Poloha domu") – o tom, jestli je dům samostatný, měl při kontrole 7. 11. 2026
/// pravdu ve všech 11 domech, kde se model mýlil; typ (řadový × rohový) ale plete.</item>
/// <item>Obrazový model nad mapou, ortofotem a fotkami – četl společnou zeď dvou půdorysů jako mezeru
/// („na všech stranách je mezera…").</item>
/// </list>
/// O samostatném × přistavěném rozhodují 2 ze 3 (<see cref="Reconcile"/>), bez katastru makléř; typ dodá
/// katastr, jinak makléř, jinak model. Výsledek se uloží ke všem členům skupiny duplicit – je to vlastnost domu.
/// </summary>
public sealed class HousePositionService(
    RealEstateDbContext db,
    IPhotoClassificationService vision,
    IDuplicateGroupService duplicateGroups,
    Vision.ICuzkMapService maps,
    Vision.ICuzkBuildingService buildings,
    IHttpClientFactory httpClientFactory,
    ILogger<HousePositionService> logger) : IHousePositionService
{
    public const int MaxPhotos = 6;
    /// <summary>S katastrální mapou a ortofotem stačí z galerie pár fotek z ulice a ze dvora.</summary>
    public const int MaxPhotosWithMaps = 4;

    public const string ScopeContradicting = "contradicting";
    public const string ScopeUnreconciled = "unreconciled";

    private const string Prompt = """
        {INTRO}
        {EVIDENCE}
        Decide how the HOUSE FOR SALE stands in relation to the MAIN buildings (houses) on NEIGHBOURING plots.

        Respond with JSON only:
        {"position":"detached","map_matches_photos":true,"reason":"..."}

        "position" - exactly one of:
          detached - no neighbour's house touches the house for sale or its wings; there is a gap, driveway or garden on every side. A neighbour's garage, shed or barn touching it, or the property's own annex touching a neighbour, still counts as detached - say so in the reason.
          semi_detached - exactly ONE side shares a wall with ONE neighbouring house that is free on its other side (a duplex, Czech "dvojdům"), or a village house whose one side wall stands against the neighbour's house while the other side is free (driveway, gap, garden)
          terraced - neighbouring houses are attached on BOTH sides (row house, continuous street front)
          corner - attached on one side only, at the END of a longer row of attached houses (Czech "rohový" or "krajní řadový")
          unknown - the images do not show the sides of the house

        Rules:
        - The property's own garage, barn, outbuildings and courtyard wings are part of the house for sale, not neighbours.
        - A village house with a gate and a closed courtyard is terraced only when neighbouring houses really adjoin on both sides.
        - On the cadastral map two pink building footprints that share a common edge are ATTACHED - a shared edge is a common wall, not a gap. Only white space between two footprints is a gap.
        - The cadastral map and the orthophoto are the best evidence. In listing photos the house for sale is usually centred or highlighted in colour.
        - If only the street facade is visible and you cannot see what is on either side, answer unknown. Never guess.

        "map_matches_photos" - when both a map/orthophoto and listing photos are given: true if the building at the crosshair is clearly the same house as in the photos (roof shape and colour, courtyard, surroundings, age of the building), false if it clearly is a different building (the listing's pin may be wrong), null when you cannot tell or no photos are given. When false, judge from the listing photos only.
        "reason" - one or two sentences in Czech saying what is on each side of the house (for example: "Vlevo vjezd a odstup od souseda, vpravo hospodářské křídlo přiléhá k sousedově stodole.").
        """;

    public async Task<HousePositionResultDto> DetectAsync(Guid listingId, CancellationToken ct)
    {
        var listing = await db.Listings.AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => new { l.Id, l.PropertyType, l.DuplicateOfListingId, l.Municipality })
            .FirstOrDefaultAsync(ct);
        if (listing is null)
            return new HousePositionResultDto(listingId, null, null, null, 0, "Inzerát nenalezen.");
        if (listing.PropertyType is not (PropertyType.House or PropertyType.Cottage))
            return new HousePositionResultDto(listingId, null, null, null, 0, "Poloha domu se určuje jen u domů a chalup.");

        var owner = (await duplicateGroups.GetGroupPhotoSetAsync(listingId, ct)).OwnerListingId;
        var photos = await LoadClassifiedAsync(owner, ct);
        if (photos.Count == 0)
        {
            // Bez kategorií nevíme, které fotky jsou venkovní – galerii nejdřív klasifikujeme
            await vision.ClassifyBatchAsync(50, ct, owner);
            photos = await LoadClassifiedAsync(owner, ct);
        }

        var rootId = listing.DuplicateOfListingId ?? listing.Id;
        var brokerListed = await BrokerClaimAsync(listing.Id, rootId, ct);

        // Katastr kolem přesné GPS: půdorysy budov (změřené společné zdi) a mapa s ortofotem pro model
        byte[]? cadastreMap = null, orthophoto = null;
        CadastreVerdict? cadastre = null;
        if (await ResolvePreciseGpsAsync(listing.Id, rootId, ct) is { } gps)
        {
            cadastreMap = await maps.CadastralMapAsync(gps.Latitude, gps.Longitude, ct);
            orthophoto = await maps.OrthophotoAsync(gps.Latitude, gps.Longitude, ct);
            if (await buildings.BuildingsAroundAsync(gps.Latitude, gps.Longitude, ct) is { } around)
                cadastre = HouseAdjacency.Analyze(around, listing.Municipality);
        }
        var hasMaps = cadastreMap is not null || orthophoto is not null;

        var selected = SelectPhotos(photos, hasMaps ? MaxPhotosWithMaps : MaxPhotos);
        if (selected.Count == 0 && !hasMaps && cadastre?.IsConclusive != true)
            return new HousePositionResultDto(listingId, null, null, null, 0, "Galerie nemá venkovní ani letecké fotky a inzerát nemá přesnou GPS pro mapu – polohu domu nejde určit.");

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");

        var images = new List<byte[]>();
        if (cadastreMap is not null) images.Add(cadastreMap);
        if (orthophoto is not null) images.Add(orthophoto);
        var hasAerial = orthophoto is not null;
        var gallery = 0;
        foreach (var photo in selected)
        {
            var bytes = await LoadAsync(http, photo, ct);
            if (bytes is null) continue;
            images.Add(bytes);
            gallery++;
            hasAerial |= IsAerial(photo);
        }

        HousePositionVerdict? verdict = null;
        if (images.Count > 0)
        {
            var raw = await vision.AskVisionAsync(images, BuildPrompt(cadastreMap is not null, orthophoto is not null, gallery, brokerListed, cadastre?.PromptFacts()), 300, ct);
            verdict = ParseVerdict(raw);

            // Špendlík inzerátu nemusí sedět (makléř dá stejnou GPS víc domům). Když model řekne, že
            // budova na mapě není dům z fotek, mapy i půdorysy z katastru zahodíme – popisují cizí
            // budovu – a rozhodne se znovu jen z galerie.
            if (verdict?.MapMatchesPhotos == false && hasMaps && gallery > 0)
            {
                logger.LogWarning("Poloha domu {ListingId}: budova na mapě neodpovídá fotkám – znovu jen z galerie", listingId);
                cadastre = null;
                var galleryImages = images.Skip(images.Count - gallery).ToList();
                hasAerial = selected.Take(gallery).Any(IsAerial);
                raw = await vision.AskVisionAsync(galleryImages, BuildPrompt(false, false, galleryImages.Count, brokerListed, null), 300, ct);
                verdict = ParseVerdict(raw);
                if (verdict is not null)
                    verdict = verdict with { Reason = $"{MapMismatchNote} {verdict.Reason}" };
            }
            if (verdict is not null)
                verdict = verdict with { Reason = WithEvidenceNote(verdict.Reason, hasAerial) };
        }

        // Bez odpovědi modelu a bez závěru z katastru není nic nového – samotný údaj makléře se neukládá
        var decision = verdict is null && cadastre?.IsConclusive != true ? null : Reconcile(cadastre, brokerListed, verdict);
        if (decision is null)
            return new HousePositionResultDto(listingId, null, null, null, images.Count, images.Count == 0
                ? "Venkovní fotky se nepodařilo načíst (zdroj je už nenabízí)."
                : "Obrazový model nevrátil použitelnou odpověď – zkuste to později.");

        // Vlastnost domu: zapíše se ke všem kopiím téhož domu, ať uživatel otevře kteroukoli
        var now = DateTime.UtcNow;
        var cadastrePosition = cadastre?.Position;
        await db.Listings
            .Where(l => l.Id == rootId || l.DuplicateOfListingId == rootId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(l => l.HousePosition, decision.Position)
                .SetProperty(l => l.HousePositionReason, decision.Reason)
                .SetProperty(l => l.HousePositionSource, decision.Source)
                .SetProperty(l => l.HousePositionCadastre, cadastrePosition)
                .SetProperty(l => l.HousePositionAt, now), ct);

        logger.LogInformation("Poloha domu {ListingId}: {Position} ({Source}; katastr {Cadastre}, makléř {Broker}, model {Model}) z {Photos} obrázků – {Reason}",
            listingId, decision.Position, decision.Source, cadastrePosition ?? "-", brokerListed ?? "-", verdict?.Position ?? "-", images.Count, decision.Reason);

        return new HousePositionResultDto(listingId, decision.Position, HousePositions.Label(decision.Position), decision.Reason, images.Count, null, decision.Source);
    }

    public async Task<List<Guid>> FindForRedetectionAsync(string scope, bool activeOnly, int limit, CancellationToken ct)
    {
        var rows = await db.Listings.AsNoTracking()
            .Where(l => l.PropertyType == PropertyType.House || l.PropertyType == PropertyType.Cottage)
            .Where(l => l.HousePosition != null || l.HousePositionListed != null)
            .Select(l => new HousePositionGroupRow(l.DuplicateOfListingId ?? l.Id, l.IsActive, l.HousePosition, l.HousePositionSource, l.HousePositionListed))
            .ToListAsync(ct);
        return SelectForRedetection(rows, scope, activeOnly, limit);
    }

    public async Task<HousePositionRedetectResultDto> RedetectAsync(IReadOnlyList<Guid> listingIds, CancellationToken ct)
    {
        var items = new List<HousePositionRedetectItemDto>();
        foreach (var id in listingIds)
        {
            ct.ThrowIfCancellationRequested();
            var before = await db.Listings.AsNoTracking().Where(l => l.Id == id).Select(l => l.HousePosition).FirstOrDefaultAsync(ct);
            try
            {
                var result = await DetectAsync(id, ct);
                items.Add(new HousePositionRedetectItemDto(id, before, result.Position ?? before, result.Source, result.Message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Poloha domu {ListingId}: nové určení selhalo", id);
                items.Add(new HousePositionRedetectItemDto(id, before, before, null, ex.Message));
            }
        }
        return new HousePositionRedetectResultDto(
            items.Count,
            items.Count(i => i.Message is null && i.After != i.Before),
            items.Count(i => i.Message is not null),
            items);
    }

    /// <summary>Údaj makléře o poloze: otevřený inzerát, jinak kterýkoli člen skupiny (aktivní a nejčerstvější první).</summary>
    private Task<string?> BrokerClaimAsync(Guid listingId, Guid rootId, CancellationToken ct)
        => db.Listings.AsNoTracking()
            .Where(l => (l.Id == rootId || l.DuplicateOfListingId == rootId) && l.HousePositionListed != null)
            .OrderByDescending(l => l.Id == listingId)
            .ThenByDescending(l => l.IsActive)
            .ThenByDescending(l => l.LastSeenAt)
            .Select(l => l.HousePositionListed)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Přesná GPS domu: vlastní, nebo od člena skupiny duplicit. Geokódovaný střed obce (Nominatim,
    /// Bazoš, iDNES…) by zaměřovač posadil na náves – pak je lepší mapu vynechat.
    /// </summary>
    private async Task<(double Latitude, double Longitude)?> ResolvePreciseGpsAsync(Guid listingId, Guid rootId, CancellationToken ct)
    {
        var approx = DuplicateDetectionService.ApproxGpsSources;
        var candidate = await db.Listings.AsNoTracking()
            .Where(l => (l.Id == rootId || l.DuplicateOfListingId == rootId)
                        && l.Latitude != null && l.Longitude != null
                        && l.GeocodeSource != "nominatim" && !approx.Contains(l.SourceCode))
            .OrderByDescending(l => l.Id == listingId)
            .ThenByDescending(l => l.IsActive)
            .Select(l => new { l.Latitude, l.Longitude })
            .FirstOrDefaultAsync(ct);
        if (candidate is null) return null;

        // Tentýž bod u jiného domu = makléřův špendlík, ne poloha domu (Lechovice 7. 10. 2026:
        // dvě nabídky téže kanceláře na jedné GPS). Mapa by pak ukázala cizí budovu.
        var sharedWithOtherHouse = await db.Listings.AsNoTracking()
            .AnyAsync(l => l.Latitude == candidate.Latitude && l.Longitude == candidate.Longitude
                           && l.Id != rootId && l.DuplicateOfListingId != rootId
                           && l.GeocodeSource != "nominatim", ct);
        if (sharedWithOtherHouse)
        {
            logger.LogInformation("Poloha domu {ListingId}: GPS sdílí s jiným inzerátem – mapa se nepoužije", listingId);
            return null;
        }
        return (candidate.Latitude!.Value, candidate.Longitude!.Value);
    }

    /// <summary>
    /// Dotaz pro obrazový model: úvod podle důkazů (mapa, ortofoto, fotky z galerie – v tomhle pořadí),
    /// pak údaj makléře a fakta z katastru, když je máme.
    /// </summary>
    public static string BuildPrompt(bool hasCadastre, bool hasOrthophoto, int galleryCount, string? brokerListed = null, string? cadastreFacts = null)
    {
        var total = (hasCadastre ? 1 : 0) + (hasOrthophoto ? 1 : 0) + galleryCount;
        var parts = new List<string> { $"These {total} images (numbered 1..{total}) describe ONE Czech family house for sale." };
        var n = 1;
        if (hasCadastre)
            parts.Add($"Image {n++} is the cadastral map (ČÚZK) of 200 × 200 m around the house: black lines are parcel boundaries, pink shapes are building footprints, numbers are parcel numbers, and the red crosshair marks the house for sale.");
        if (hasOrthophoto)
            parts.Add($"Image {n++} is the aerial orthophoto of the same 200 × 200 m area with the same red crosshair on the house for sale.");
        if (galleryCount > 0)
            parts.Add(galleryCount == 1
                ? $"Image {n} is a photo from the listing (exterior, garden or aerial)."
                : $"Images {n}..{n + galleryCount - 1} are photos from the listing (exterior, garden or aerial).");

        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(brokerListed))
        {
            var meaning = BrokerPosition(brokerListed) switch
            {
                HousePositions.Detached => "detached",
                HousePositions.Terraced => "attached - row or block",
                HousePositions.Corner => "attached on one side - end of a row",
                HousePositions.SemiDetached => "attached on one side - duplex",
                _ => "unrecognised value",
            };
            evidence.Add($"The broker states the house position as \"{brokerListed.Trim()}\" ({meaning}). Brokers are usually right about whether the house is attached or detached, but often imprecise about the exact type - verify it in the images and say in the reason when the images contradict it.");
        }
        if (!string.IsNullOrWhiteSpace(cadastreFacts))
            evidence.Add(cadastreFacts);

        return Prompt
            .Replace("{INTRO}", string.Join(" ", parts))
            .Replace("{EVIDENCE}", evidence.Count == 0 ? "" : string.Join("\n", evidence) + "\n");
    }

    private Task<List<ListingPhoto>> LoadClassifiedAsync(Guid ownerListingId, CancellationToken ct)
        => db.ListingPhotos.AsNoTracking()
            .Where(p => p.ListingId == ownerListingId && p.PhotoCategory != null)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Údaj makléře → poloha. Sreality „Poloha domu" má hodnoty Samostatný / Řadový / Rohový / V bloku;
    /// „Rohový" znamená krajní dům řady (přistavěný z jedné strany), „V bloku" dům v souvislé zástavbě.
    /// Nerozpoznaná hodnota = null (nehlasuje).
    /// </summary>
    public static string? BrokerPosition(string? listed)
    {
        if (string.IsNullOrWhiteSpace(listed)) return null;
        var text = DuplicateDetectionService.NormalizeText(listed);
        if (text.StartsWith("samostat", StringComparison.Ordinal)) return HousePositions.Detached;
        if (text.StartsWith("radov", StringComparison.Ordinal) || text.Contains("blok", StringComparison.Ordinal)) return HousePositions.Terraced;
        if (text.StartsWith("rohov", StringComparison.Ordinal) || text.StartsWith("krajn", StringComparison.Ordinal)) return HousePositions.Corner;
        if (text.StartsWith("dvojd", StringComparison.Ordinal) || text.StartsWith("prisazen", StringComparison.Ordinal) || text.StartsWith("pristaven", StringComparison.Ordinal))
            return HousePositions.SemiDetached;
        return null;
    }

    /// <summary>
    /// Sladí katastr, makléře a obrazový model. O samostatném × přistavěném:
    /// <list type="bullet">
    /// <item>s katastrem rozhoduje většina hlasujících (2 ze 3); při remíze 1 : 1 dům samostatný není –
    /// katastr sám makléřovo „Řadový" nepřebije, makléřovo „Samostatný" sám nevyvrátí;</item>
    /// <item>bez katastru rozhoduje makléř (při kontrole 7. 11. 2026 měl pravdu u všech 11 domů, kde se model
    /// mýlil), bez makléře model.</item>
    /// </list>
    /// Typ přistavění (řadový / rohový / dvojdům) dodá katastr, pak makléř, nakonec model – při kontrole
    /// 7. 11. 2026 trefil makléř typ častěji než model (Čejkovice „Rohový" = konec řady, model „semi").
    /// Null = není z čeho rozhodnout.
    /// </summary>
    public static HousePositionDecision? Reconcile(CadastreVerdict? cadastre, string? brokerListed, HousePositionVerdict? model)
    {
        var g = cadastre?.Position;
        var b = BrokerPosition(brokerListed);
        var m = model?.Position is { } p && p != HousePositions.Unknown ? p : null;

        if (g is null && model is null) return null;
        if (g is null && m is null && b is null)
            return new HousePositionDecision(HousePositions.Unknown, JoinReason(null, cadastre, model), HousePositionSources.Vision);

        bool detached;
        if (g is null)
            detached = (b ?? m) == HousePositions.Detached;
        else
        {
            var votes = new[] { g, b, m }.Where(v => v is not null).ToList();
            detached = votes.Count(v => v == HousePositions.Detached) * 2 > votes.Count;
        }

        var position = detached
            ? HousePositions.Detached
            : new[] { g, b, m }.First(v => v is not null && v != HousePositions.Detached)!;

        var source = g is not null && (g == HousePositions.Detached) == detached ? HousePositionSources.Cadastre
            : b is not null && (b == HousePositions.Detached) == detached ? HousePositionSources.Broker
            : HousePositionSources.Vision;

        var voters = new List<(string Name, bool Detached)>();
        if (g is not null) voters.Add(("katastr", g == HousePositions.Detached));
        if (b is not null) voters.Add(($"makléř („{brokerListed!.Trim()}“)", b == HousePositions.Detached));
        if (m is not null) voters.Add(("snímky", m == HousePositions.Detached));

        string? note = null;
        if (voters.Select(v => v.Detached).Distinct().Count() > 1)
        {
            var free = string.Join(" a ", voters.Where(v => v.Detached).Select(v => v.Name));
            var joined = string.Join(" a ", voters.Where(v => !v.Detached).Select(v => v.Name));
            var why = g is null ? "bez katastru rozhoduje makléř"
                : voters.Count == 2 ? "bez shody dvou zdrojů se dům nepočítá jako samostatný"
                : "shoda dvou zdrojů ze tří";
            note = $"Rozpor – samostatný: {free}; přistavěný: {joined}. Platí {HousePositions.Label(position)} ({why}).";
        }
        else if (g is null && m is null)
            note = $"Ze snímků polohu určit nejde – platí údaj makléře („{brokerListed!.Trim()}“).";

        return new HousePositionDecision(position, JoinReason(note, cadastre, model), source);
    }

    private static string JoinReason(string? note, CadastreVerdict? cadastre, HousePositionVerdict? model)
    {
        var reason = string.Join(" ", new[] { note, cadastre?.Reason, model?.Reason }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return reason.Length > 1000 ? reason[..1000] : reason;
    }

    /// <summary>Uložená poloha odporuje makléři v tom podstatném – samostatný × přistavěný.</summary>
    public static bool ContradictsBroker(string? position, string? brokerListed)
    {
        var broker = BrokerPosition(brokerListed);
        if (broker is null || position is null || position == HousePositions.Unknown || !HousePositions.All.Contains(position))
            return false;
        return (position == HousePositions.Detached) != (broker == HousePositions.Detached);
    }

    /// <summary>
    /// Výběr domů k novému určení z řádků inzerátů (skupina duplicit = jeden dům). Rozpor, o kterém rozhodl
    /// katastr se snímky, je záměr a znovu se nepouští. Aktivní a rozporné domy první.
    /// </summary>
    public static List<Guid> SelectForRedetection(IEnumerable<HousePositionGroupRow> rows, string scope, bool activeOnly, int limit)
        => rows
            .GroupBy(r => r.RootId)
            .Select(g => new
            {
                Root = g.Key,
                Active = g.Any(r => r.IsActive),
                Position = g.Select(r => r.Position).FirstOrDefault(v => v is not null),
                Source = g.Where(r => r.Position is not null).Select(r => r.Source).FirstOrDefault(v => v is not null),
                Listed = g.Select(r => r.Listed).FirstOrDefault(v => v is not null),
            })
            .Where(h => h.Position is not null && (!activeOnly || h.Active))
            .Select(h => new
            {
                h.Root,
                h.Active,
                Contradicts = h.Source != HousePositionSources.Cadastre && ContradictsBroker(h.Position, h.Listed),
                Unreconciled = h.Source is null,
            })
            .Where(h => h.Contradicts || (scope == ScopeUnreconciled && h.Unreconciled))
            .OrderByDescending(h => h.Active)
            .ThenByDescending(h => h.Contradicts)
            .ThenBy(h => h.Root)
            .Take(Math.Max(0, limit))
            .Select(h => h.Root)
            .ToList();

    /// <summary>
    /// Fotky, ze kterých je poloha domu vidět: nejdřív letecké, pak ostatní exteriér, nakonec pozemek.
    /// Vizualizace se nepočítají – render sousedy často vynechá.
    /// </summary>
    public static List<ListingPhoto> SelectPhotos(IEnumerable<ListingPhoto> photos, int max)
        => photos
            .Where(p => p.PhotoCategory is "exterior" or "land")
            .Where(p => !IsVisualization(p))
            .OrderBy(Rank)
            .ThenBy(p => p.Order)
            .Take(max)
            .ToList();

    private static int Rank(ListingPhoto photo)
        => IsAerial(photo) ? 0 : photo.PhotoCategory == "exterior" ? 1 : 2;

    /// <summary>Letecký snímek podle popisu z klasifikace – jediný záběr, na kterém jsou vidět obě strany domu.</summary>
    public static bool IsAerial(ListingPhoto photo)
    {
        var description = photo.PhotoDescription ?? "";
        return description.Contains("leteck", StringComparison.OrdinalIgnoreCase)
               || description.Contains("dron", StringComparison.OrdinalIgnoreCase)
               || description.Contains("z ptačí", StringComparison.OrdinalIgnoreCase);
    }

    public const string NoAerialNote = "Bez leteckého snímku i mapy – méně jisté.";
    public const string MapMismatchNote = "GPS inzerátu ukazuje na jinou budovu, mapa se nepoužila.";

    /// <summary>Z fotek z ulice a ze dvora model boky domu spíš odhaduje – zdůvodnění to musí říct.</summary>
    public static string WithEvidenceNote(string reason, bool hasAerial)
        => hasAerial ? reason : string.IsNullOrWhiteSpace(reason) ? NoAerialNote : $"{NoAerialNote} {reason}";

    private static bool IsVisualization(ListingPhoto photo)
        => (photo.PhotoLabels ?? "").Contains(PhotoClassificationService.VisualizationLabel, StringComparison.OrdinalIgnoreCase)
           || (photo.PhotoDescription ?? "").StartsWith("Vizualizace", StringComparison.OrdinalIgnoreCase);

    /// <summary>Odpověď modelu → poloha a zdůvodnění; neznámá hodnota nebo nečitelná odpověď = null.</summary>
    public static HousePositionVerdict? ParseVerdict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("position", out var p)
                || p.ValueKind != JsonValueKind.String)
                return null;

            var position = p.GetString()!.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            if (!HousePositions.All.Contains(position)) return null;

            var reason = doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()!.Trim()
                : "";
            bool? mapMatches = doc.RootElement.TryGetProperty("map_matches_photos", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? m.GetBoolean()
                : null;
            return new HousePositionVerdict(position, reason.Length > 500 ? reason[..500] : reason, mapMatches);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]?> LoadAsync(HttpClient http, ListingPhoto photo, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(photo.StoredUrl))
            {
                var path = vision.ResolveInspectionPhotoPath(photo.StoredUrl);
                if (File.Exists(path)) return await File.ReadAllBytesAsync(path, ct);
            }
            if (string.IsNullOrWhiteSpace(photo.OriginalUrl)) return null;

            using var response = await http.GetAsync(photo.OriginalUrl, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogDebug(ex, "Fotku {PhotoId} se nepodařilo načíst", photo.Id);
            return null;
        }
    }
}
