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

public sealed record HousePositionVerdict(string Position, string Reason);

public sealed record HousePositionResultDto(Guid ListingId, string? Position, string? Label, string? Reason, int PhotosUsed, string? Message);

public interface IHousePositionService
{
    /// <summary>Určí z venkovních a leteckých fotek, jak dům stojí vůči sousedům, a uloží to ke všem kopiím inzerátu.</summary>
    Task<HousePositionResultDto> DetectAsync(Guid listingId, CancellationToken ct);
}

/// <summary>
/// Poloha domu: samostatný / přisazený z jedné strany / řadový / rohový. Zdroje ji buď neuvádějí,
/// nebo ji makléř vyplní po svém (Lechovice 6. 10. 2026: „Řadový" u domu s vjezdem a předzahrádkou),
/// a hodnocení pak označilo za řadovku cokoli, co se dotýká souseda. Venkovní a letecké fotky proto
/// jdou obrazovému modelu společně a výsledek se uloží ke všem členům skupiny duplicit – je to
/// vlastnost domu, ne kopie inzerátu.
/// </summary>
public sealed class HousePositionService(
    RealEstateDbContext db,
    IPhotoClassificationService vision,
    IDuplicateGroupService duplicateGroups,
    Vision.ICuzkMapService maps,
    IHttpClientFactory httpClientFactory,
    ILogger<HousePositionService> logger) : IHousePositionService
{
    public const int MaxPhotos = 6;
    /// <summary>S katastrální mapou a ortofotem stačí z galerie pár fotek z ulice a ze dvora.</summary>
    public const int MaxPhotosWithMaps = 4;

    private const string Prompt = """
        {INTRO}

        Decide how the HOUSE FOR SALE stands in relation to buildings on NEIGHBOURING plots.

        Respond with JSON only:
        {"position":"detached","reason":"..."}

        "position" - exactly one of:
          detached - no neighbour's building touches the house or its wings; there is a gap, driveway or garden on every side
          semi_detached - exactly ONE side touches a neighbour's building: half of a duplex, or a village house whose one side wall or courtyard wing stands against the neighbour's house or outbuilding while the other side is free (driveway, gap, garden)
          terraced - neighbouring houses are attached on BOTH sides (row house, continuous street front)
          corner - the end of a row at a street corner: attached to a neighbour on one side, the other side faces a second street
          unknown - the images do not show the sides of the house

        Rules:
        - The property's own garage, barn, outbuildings and courtyard wings are part of the house for sale, not neighbours.
        - A village house with a gate and a closed courtyard is terraced only when neighbours really adjoin on both sides.
        - The cadastral map and the orthophoto are the best evidence: a building footprint that shares an edge with a footprint on a neighbouring parcel is attached. In listing photos the house for sale is usually centred or highlighted in colour.
        - If only the street facade is visible and you cannot see what is on either side, answer unknown. Never guess.

        "reason" - one or two sentences in Czech saying what is on each side of the house (for example: "Vlevo vjezd a odstup od souseda, vpravo hospodářské křídlo přiléhá k sousedově stodole.").
        """;

    public async Task<HousePositionResultDto> DetectAsync(Guid listingId, CancellationToken ct)
    {
        var listing = await db.Listings.AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => new { l.Id, l.PropertyType, l.DuplicateOfListingId })
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

        // Katastrální mapa a ortofoto ČÚZK kolem přesné GPS – jediný důkaz, který má každý dům
        var rootId = listing.DuplicateOfListingId ?? listing.Id;
        byte[]? cadastre = null, orthophoto = null;
        if (await ResolvePreciseGpsAsync(listing.Id, rootId, ct) is { } gps)
        {
            cadastre = await maps.CadastralMapAsync(gps.Latitude, gps.Longitude, ct);
            orthophoto = await maps.OrthophotoAsync(gps.Latitude, gps.Longitude, ct);
        }
        var hasMaps = cadastre is not null || orthophoto is not null;

        var selected = SelectPhotos(photos, hasMaps ? MaxPhotosWithMaps : MaxPhotos);
        if (selected.Count == 0 && !hasMaps)
            return new HousePositionResultDto(listingId, null, null, null, 0, "Galerie nemá venkovní ani letecké fotky a inzerát nemá přesnou GPS pro mapu – polohu domu nejde určit.");

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");

        var images = new List<byte[]>();
        if (cadastre is not null) images.Add(cadastre);
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
        if (images.Count == 0)
            return new HousePositionResultDto(listingId, null, null, null, 0, "Venkovní fotky se nepodařilo načíst (zdroj je už nenabízí).");

        var raw = await vision.AskVisionAsync(images, BuildPrompt(cadastre is not null, orthophoto is not null, gallery), 300, ct);
        var verdict = ParseVerdict(raw);
        if (verdict is not null)
            verdict = verdict with { Reason = WithEvidenceNote(verdict.Reason, hasAerial) };
        if (verdict is null)
            return new HousePositionResultDto(listingId, null, null, null, images.Count, "Obrazový model nevrátil použitelnou odpověď – zkuste to později.");

        // Vlastnost domu: zapíše se ke všem kopiím téhož domu, ať uživatel otevře kteroukoli
        var now = DateTime.UtcNow;
        await db.Listings
            .Where(l => l.Id == rootId || l.DuplicateOfListingId == rootId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(l => l.HousePosition, verdict.Position)
                .SetProperty(l => l.HousePositionReason, verdict.Reason)
                .SetProperty(l => l.HousePositionAt, now), ct);

        logger.LogInformation("Poloha domu {ListingId}: {Position} z {Photos} fotek – {Reason}",
            listingId, verdict.Position, images.Count, verdict.Reason);

        return new HousePositionResultDto(listingId, verdict.Position, HousePositions.Label(verdict.Position), verdict.Reason, images.Count, null);
    }

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
        return candidate is null ? null : (candidate.Latitude!.Value, candidate.Longitude!.Value);
    }

    /// <summary>Úvod dotazu podle toho, které důkazy máme: mapa, ortofoto, fotky z galerie (v tomhle pořadí).</summary>
    public static string BuildPrompt(bool hasCadastre, bool hasOrthophoto, int galleryCount)
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
        return Prompt.Replace("{INTRO}", string.Join(" ", parts));
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
            return new HousePositionVerdict(position, reason.Length > 500 ? reason[..500] : reason);
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
