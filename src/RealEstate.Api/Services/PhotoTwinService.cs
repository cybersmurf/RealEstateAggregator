using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Services.Duplicates;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>Dvojice fotek ze stejného místa a úhlu; <see cref="Edited"/> = která z nich je upravená (pořadí ve vzorku).</summary>
public sealed record PhotoTwin(int A, int B, int? Edited, string Reason);

public sealed record PhotoTwinResultDto(Guid ListingId, int PhotosChecked, int GroupsChecked, int TwinsFound, string? Message);

public interface IPhotoTwinService
{
    /// <summary>Najde v galerii inzerátu „dvojčata" – stejný záběr, jiný interiér (retuš, virtuální zařízení, vizualizace).</summary>
    Task<PhotoTwinResultDto> DetectAsync(Guid listingId, CancellationToken ct);
}

/// <summary>
/// „Dvojčata" v galerii: dvě fotky ze stejného místa a úhlu, ale s jiným interiérem nebo povrchy.
/// Klasifikace hodnotí každou fotku zvlášť a upravený záběr tak pozná jen někdy – teprve vedle
/// originálu je vidět, že jde o retuš, virtuální zařízení nebo vizualizaci. Fotky jedné kategorie
/// proto jdou modelu společně; nalezené dvojice dostanou štítek „twin" a upravená navíc
/// „visualization" (analýza ji pak nebere jako skutečný stav).
/// </summary>
public sealed class PhotoTwinService(
    RealEstateDbContext db,
    IPhotoClassificationService vision,
    IDuplicateGroupService duplicateGroups,
    IHttpClientFactory httpClientFactory,
    ILogger<PhotoTwinService> logger) : IPhotoTwinService
{
    public const string TwinLabel = "twin";
    public const string TwinNotePrefix = "Dvojče fotky č. ";

    private const int ChunkSize = 8;
    private const int ChunkOverlap = 2;

    /// <summary>Kategorie, kde má hledání smysl (pozemky, půdorysy a detaily vad ne).</summary>
    public static readonly string[] TwinCategories =
        ["living_room", "kitchen", "bathroom", "bedroom", "interior", "attic", "exterior"];

    private const string TwinPrompt = """
        These {N} images (numbered 1..{N}) come from ONE Czech real-estate listing and are all labelled "{CATEGORY}".

        Find "twins": pairs of images taken from the SAME camera position that show the SAME room or facade (same walls, windows, doors, ceiling and perspective) but with a different interior or finish - different or missing furniture, different floor or wall colours, repaired versus damaged surfaces. Such a pair means one image is a retouch, a virtual staging or a render of the other.

        NOT twins: two ordinary photos of the same room from different angles; two different rooms; near-identical duplicates with no visible difference.

        Respond with JSON only:
        {"twins":[{"a":1,"b":4,"edited":4,"reason":"..."}]}

        "a", "b" - image numbers of the pair
        "edited" - the number of the image that is the edited, staged or rendered version, or null when you cannot tell
        "reason" - one sentence in Czech: what is identical (the composition) and what differs

        An empty "twins" array is the expected answer for most listings. Never guess.
        """;

    public async Task<PhotoTwinResultDto> DetectAsync(Guid listingId, CancellationToken ct)
    {
        var owner = (await duplicateGroups.GetGroupPhotoSetAsync(listingId, ct)).OwnerListingId;
        var photos = await db.ListingPhotos
            .Where(p => p.ListingId == owner && p.PhotoCategory != null)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);
        if (photos.Count < 2)
            return new PhotoTwinResultDto(owner, photos.Count, 0, 0, "Inzerát nemá klasifikované fotky – nejdřív spusťte klasifikaci.");

        // Opakovaný běh začíná načisto: staré štítky a poznámky o dvojčatech pryč
        foreach (var photo in photos)
            ClearTwinMarks(photo);

        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");

        int groups = 0, found = 0, checkedPhotos = 0;
        var seenPairs = new HashSet<(Guid, Guid)>();

        foreach (var category in TwinCategories)
        {
            var inCategory = photos.Where(p => p.PhotoCategory == category).ToList();
            if (inCategory.Count < 2) continue;

            foreach (var chunk in Chunk(inCategory, ChunkSize, ChunkOverlap))
            {
                ct.ThrowIfCancellationRequested();

                var loaded = new List<(ListingPhoto Photo, byte[] Bytes)>();
                foreach (var photo in chunk)
                {
                    var bytes = await LoadAsync(http, photo, ct);
                    if (bytes is not null) loaded.Add((photo, bytes));
                }
                if (loaded.Count < 2) continue;

                groups++;
                checkedPhotos += loaded.Count;
                var prompt = TwinPrompt.Replace("{N}", loaded.Count.ToString()).Replace("{CATEGORY}", category);
                var raw = await vision.AskVisionAsync(loaded.Select(l => l.Bytes).ToList(), prompt, 500, ct);

                foreach (var twin in ParseTwins(raw, loaded.Count))
                {
                    var (a, b) = (loaded[twin.A - 1].Photo, loaded[twin.B - 1].Photo);
                    var key = a.Order < b.Order ? (a.Id, b.Id) : (b.Id, a.Id);
                    if (!seenPairs.Add(key)) continue;   // tatáž dvojice z překryvu dávek

                    var edited = twin.Edited is { } e ? loaded[e - 1].Photo : null;
                    MarkTwin(a, b, edited == a, twin.Reason);
                    MarkTwin(b, a, edited == b, twin.Reason);
                    found++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Dvojčata v galerii {ListingId}: {Photos} fotek v {Groups} dávkách, {Found} dvojic",
            owner, checkedPhotos, groups, found);

        return new PhotoTwinResultDto(owner, checkedPhotos, groups, found, null);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Dávky po <paramref name="size"/> s překryvem – dvojče bývá v galerii hned vedle originálu.</summary>
    public static List<List<T>> Chunk<T>(IReadOnlyList<T> items, int size, int overlap)
    {
        var chunks = new List<List<T>>();
        if (items.Count <= size)
        {
            chunks.Add(items.ToList());
            return chunks;
        }
        for (var start = 0; start < items.Count; start += size - overlap)
        {
            chunks.Add(items.Skip(start).Take(size).ToList());
            if (start + size >= items.Count) break;
        }
        return chunks;
    }

    /// <summary>Odpověď modelu → platné dvojice (čísla v rozsahu 1..count, různé fotky).</summary>
    public static List<PhotoTwin> ParseTwins(string? raw, int count)
    {
        var twins = new List<PhotoTwin>();
        if (string.IsNullOrWhiteSpace(raw)) return twins;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("twins", out var array)
                || array.ValueKind != JsonValueKind.Array)
                return twins;

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var (a, b) = (Number(item, "a"), Number(item, "b"));
                if (a is null || b is null || a == b || a > count || b > count) continue;

                var edited = Number(item, "edited");
                if (edited != a && edited != b) edited = null;
                var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()!.Trim()
                    : "";
                twins.Add(new PhotoTwin(a.Value, b.Value, edited, reason));
            }
        }
        catch (JsonException)
        {
            // nepoužitelná odpověď = žádná dvojčata
        }
        return twins;
    }

    /// <summary>Označí fotku jako dvojče jiné; upravená verze dostane i štítek vizualizace.</summary>
    public static void MarkTwin(ListingPhoto photo, ListingPhoto other, bool isEdited, string reason)
    {
        var labels = ReadLabels(photo.PhotoLabels);
        if (!labels.Contains(TwinLabel)) labels.Add(TwinLabel);
        if (isEdited && !labels.Contains(PhotoClassificationService.VisualizationLabel))
            labels.Insert(0, PhotoClassificationService.VisualizationLabel);
        photo.PhotoLabels = JsonSerializer.Serialize(labels);

        var note = $"{TwinNotePrefix}{other.Order + 1}"
                   + (isEdited ? " – tahle verze je upravená" : "")
                   + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason.TrimEnd('.')}.");
        photo.PhotoDescription = string.IsNullOrWhiteSpace(photo.PhotoDescription)
            ? note
            : $"{photo.PhotoDescription.TrimEnd()}\n{note}";

        // Upravený záběr neukazuje skutečný stav – „poškození" na něm nic neznamená
        if (isEdited) photo.DamageDetected = false;
    }

    /// <summary>Odstraní štítek a poznámky z předchozího hledání (štítek vizualizace nechává – mohl ho dát i klasifikátor).</summary>
    public static void ClearTwinMarks(ListingPhoto photo)
    {
        var labels = ReadLabels(photo.PhotoLabels);
        if (labels.Remove(TwinLabel))
            photo.PhotoLabels = labels.Count > 0 ? JsonSerializer.Serialize(labels) : null;

        if (photo.PhotoDescription?.Contains(TwinNotePrefix, StringComparison.Ordinal) == true)
        {
            var kept = photo.PhotoDescription
                .Split('\n')
                .Where(line => !line.StartsWith(TwinNotePrefix, StringComparison.Ordinal))
                .ToList();
            photo.PhotoDescription = kept.Count > 0 ? string.Join('\n', kept).TrimEnd() : null;
        }
    }

    private static List<string> ReadLabels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static int? Number(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0
            ? n
            : null;

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
