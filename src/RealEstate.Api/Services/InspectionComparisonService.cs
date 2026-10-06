using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Services.Duplicates;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

public sealed record PhotoFinding(string Type, string Severity, string Description, int? ListingPhoto, int? InspectionPhoto);

public sealed record PhotoComparisonDto(
    string Category, string CategoryLabel, int ListingPhotoCount, int InspectionPhotoCount,
    string? Summary, List<PhotoFinding> Findings, DateTime CreatedAt);

public sealed record InspectionComparisonResultDto(
    Guid ListingId, int CategoriesCompared, int Findings, int InspectionPhotosClassified,
    List<string> NotShownInListing, List<string> NotPhotographedAtViewing, Guid? AnalysisId, string? Message);

public sealed record FindingTypeCountDto(string Type, string Label, int Count, int Listings, int High);

public sealed record FindingExampleDto(Guid ListingId, string ListingTitle, string Category, string Type, string Severity, string Description);

public sealed record InspectionFindingsSummaryDto(
    int Listings, int Comparisons, int Findings, List<FindingTypeCountDto> ByType, List<FindingExampleDto> Examples);

public interface IInspectionComparisonService
{
    /// <summary>Porovná fotky z inzerátu s fotkami z prohlídky a uloží nálezy + zprávu do analýz.</summary>
    Task<InspectionComparisonResultDto> CompareAsync(Guid listingId, bool force, CancellationToken ct);

    Task<List<PhotoComparisonDto>> GetAsync(Guid listingId, CancellationToken ct);

    /// <summary>Co se mezi inzeráty a skutečností liší nejčastěji – součet přes všechny porovnané domy.</summary>
    Task<InspectionFindingsSummaryDto> GetSummaryAsync(CancellationToken ct);
}

/// <summary>
/// Porovnání „inzerát vs. skutečnost": fotky z inzerátu a fotky z prohlídky téže kategorie
/// (kuchyň, exteriér…) jdou společně obrazovému modelu, který pojmenuje rozdíly – vadu mimo záběr,
/// retuš, přesvětlení, širokoúhlý záběr, jiné zařízení. Nálezy jsou strukturované, aby šlo napříč
/// navštívenými domy spočítat, co se v inzerátech upravuje nejčastěji.
/// </summary>
public sealed class InspectionComparisonService(
    RealEstateDbContext db,
    IPhotoClassificationService vision,
    IDuplicateGroupService duplicateGroups,
    IRagService rag,
    IHttpClientFactory httpClientFactory,
    ILogger<InspectionComparisonService> logger) : IInspectionComparisonService
{
    public const string AnalysisSource = "photo-comparison";
    public const string OmittedType = "omitted";

    private const int MaxListingPhotos = 4;
    private const int MaxInspectionPhotos = 6;

    /// <summary>Kategorie, které má smysl porovnávat (půdorys, „ostatní" a detail vady ne).</summary>
    public static readonly string[] ComparableCategories =
        ["exterior", "land", "living_room", "kitchen", "bathroom", "bedroom", "interior", "attic", "basement", "garage"];

    public static readonly IReadOnlyDictionary<string, string> CategoryLabels = new Dictionary<string, string>
    {
        ["exterior"] = "Exteriér", ["land"] = "Pozemek a zahrada", ["living_room"] = "Obývací pokoj",
        ["kitchen"] = "Kuchyň", ["bathroom"] = "Koupelna a WC", ["bedroom"] = "Ložnice a pokoje",
        ["interior"] = "Interiér (chodby, schodiště)", ["attic"] = "Půda a podkroví",
        ["basement"] = "Sklep", ["garage"] = "Garáž a dílna",
    };

    public static readonly IReadOnlyDictionary<string, string> FindingLabels = new Dictionary<string, string>
    {
        ["hidden_defect"] = "Vada, kterou inzerát neukazuje",
        ["retouched"] = "Retuš (odstraněné vady, předměty, přebarvení)",
        ["brightened"] = "Přesvětlení a přibarvení",
        ["wide_angle"] = "Širokoúhlý záběr zvětšuje prostor",
        ["staged"] = "Jiné zařízení nebo úklid než ve skutečnosti",
        ["outdated"] = "Starší fotka, stav se změnil",
        ["visualization"] = "Vizualizace místo fotografie",
        [OmittedType] = "Část domu inzerát vůbec neukazuje",
        ["other"] = "Jiný rozdíl",
    };

    private const string ComparisonPrompt = """
        You compare photos of the SAME Czech property. The first {L} image(s) come from the sales LISTING (numbered L1..L{L}). The following {V} image(s) were taken by the BUYER during a viewing (numbered V1..V{V}). All are labelled "{CATEGORY}".

        Find how the listing photos differ from reality. Report only differences you can actually see in these images. If the two groups show different rooms or cannot be matched, set "same_place" to false and return no findings. Never guess.

        Respond with JSON only:
        {"same_place":true,"summary":"...","findings":[{"type":"...","severity":"low","listing_photo":1,"viewing_photo":2,"description":"..."}]}

        "type" - exactly one of:
        hidden_defect - a defect visible at the viewing (damp, mold, cracks, peeling plaster, damaged floor, roof or windows) that the listing photos avoid or do not show
        retouched - the listing photo was edited: defects, stains, cables or objects removed, surfaces repainted
        brightened - the listing photo is much brighter, more saturated or has a different white balance than reality
        wide_angle - the listing uses a wide-angle lens or perspective that makes the space look larger than it is
        staged - furniture, decoration or tidiness in the listing differs from the real state (including virtual staging)
        outdated - the listing photo is clearly older: different season, equipment or construction stage
        visualization - the listing image is a render, not a photograph

        "severity" - high = affects the price or needs repair; medium = misleading impression; low = cosmetic
        "listing_photo" / "viewing_photo" - 1-based index within its group, or null
        "description" - one factual sentence in Czech naming what differs and where
        "summary" - 1-2 sentences in Czech; when nothing differs, say that the listing matches reality

        An empty "findings" array is a good answer. Do not invent differences.
        """;

    public async Task<InspectionComparisonResultDto> CompareAsync(Guid listingId, bool force, CancellationToken ct)
    {
        var memberIds = await GroupMemberIdsAsync(listingId, ct);

        var inspectionOwners = await db.UserListingPhotos
            .AsNoTracking()
            .Where(p => memberIds.Contains(p.ListingId))
            .GroupBy(p => p.ListingId)
            .Select(g => new { ListingId = g.Key, Count = g.Count(), Unclassified = g.Count(p => p.ClassifiedAt == null) })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);
        if (inspectionOwners.Count == 0)
            return Empty(listingId, "Inzerát nemá žádné fotky z prohlídky.");

        // Záznam se vede u inzerátu, na kterém fotky z prohlídky jsou (může to být stažená kopie)
        var recordListingId = inspectionOwners[0].ListingId;

        if (!force && await db.ListingPhotoComparisons.AnyAsync(c => c.ListingId == recordListingId, ct))
        {
            var existing = await GetAsync(recordListingId, ct);
            return new InspectionComparisonResultDto(recordListingId, existing.Count(c => c.ListingPhotoCount > 0),
                existing.Sum(c => c.Findings.Count), 0, [], [], null,
                "Porovnání už existuje – pro nové spusťte s force=true.");
        }

        // 1) Fotky z prohlídky potřebují kategorii – podle ní se párují s fotkami z inzerátu
        var classified = 0;
        foreach (var owner in inspectionOwners.Where(o => o.Unclassified > 0))
        {
            var result = await vision.ClassifyInspectionBatchAsync(50, ct, owner.ListingId);
            classified += result.Succeeded;
            if (result.Error is not null)
                logger.LogWarning("Klasifikace fotek z prohlídky {ListingId} skončila předčasně: {Error}", owner.ListingId, result.Error);
        }

        var inspectionPhotos = await db.UserListingPhotos
            .AsNoTracking()
            .Where(p => memberIds.Contains(p.ListingId) && p.PhotoCategory != null)
            .OrderBy(p => p.TakenAt).ThenBy(p => p.OriginalFileName)
            .ToListAsync(ct);
        if (inspectionPhotos.Count == 0)
            return Empty(recordListingId, "Fotky z prohlídky se nepodařilo klasifikovat (obrazový model neodpovídá nebo soubory chybí).");

        // 2) Fotky z inzerátu: člen skupiny s nejvíc klasifikovanými fotkami; když žádný, klasifikovat
        var listingPhotos = await LoadClassifiedListingPhotosAsync(memberIds, ct);
        if (listingPhotos.Count == 0)
        {
            var owner = (await duplicateGroups.GetGroupPhotoSetAsync(recordListingId, ct)).OwnerListingId;
            await vision.ClassifyBatchAsync(50, ct, owner);
            listingPhotos = await LoadClassifiedListingPhotosAsync(memberIds, ct);
        }
        if (listingPhotos.Count == 0)
            return Empty(recordListingId, "Fotky z inzerátu nejsou k dispozici (inzerát je nemá nebo už nejdou stáhnout).", classified);

        // 3) Porovnání po kategoriích
        var old = await db.ListingPhotoComparisons.Where(c => c.ListingId == recordListingId).ToListAsync(ct);
        db.ListingPhotoComparisons.RemoveRange(old);

        var rows = new List<ListingPhotoComparison>();
        var notShown = new List<string>();
        var notPhotographed = new List<string>();
        using var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");

        foreach (var category in ComparableCategories)
        {
            ct.ThrowIfCancellationRequested();
            var fromListing = listingPhotos.Where(p => p.PhotoCategory == category).ToList();
            var fromViewing = inspectionPhotos.Where(p => p.PhotoCategory == category).ToList();

            if (fromListing.Count == 0 && fromViewing.Count == 0) continue;

            if (fromViewing.Count == 0)
            {
                notPhotographed.Add(CategoryLabels[category]);
                continue;
            }

            if (fromListing.Count == 0)
            {
                // „Interiér" je sběrná kategorie – chybějící chodba v inzerátu nic neznamená
                if (category == "interior" || fromViewing.Count < 2) continue;
                notShown.Add(CategoryLabels[category]);
                rows.Add(new ListingPhotoComparison
                {
                    ListingId = recordListingId,
                    Category = category,
                    InspectionPhotoCount = fromViewing.Count,
                    Summary = $"Inzerát tuto část vůbec neukazuje, na prohlídce jste ji vyfotili {fromViewing.Count}×.",
                    Findings = SerializeFindings(
                    [
                        new PhotoFinding(OmittedType, "medium",
                            $"{CategoryLabels[category]}: v inzerátu není ani jedna fotka.", null, null),
                    ]),
                });
                continue;
            }

            var listingSample = PickEvenly(fromListing, MaxListingPhotos);
            var viewingSample = PickForViewing(fromViewing, MaxInspectionPhotos);

            var images = new List<byte[]>();
            var loadedListing = 0;
            foreach (var photo in listingSample)
            {
                var bytes = await LoadListingPhotoAsync(http, photo, ct);
                if (bytes is null) continue;
                images.Add(bytes);
                loadedListing++;
            }

            var loadedViewing = 0;
            foreach (var photo in viewingSample)
            {
                var path = vision.ResolveInspectionPhotoPath(photo.StoredUrl);
                if (!File.Exists(path)) continue;
                images.Add(await File.ReadAllBytesAsync(path, ct));
                loadedViewing++;
            }

            if (loadedListing == 0 || loadedViewing == 0)
            {
                logger.LogWarning("Porovnání {ListingId}/{Category}: fotky se nepodařilo načíst (inzerát {L}, prohlídka {V})",
                    recordListingId, category, loadedListing, loadedViewing);
                continue;
            }

            var prompt = ComparisonPrompt
                .Replace("{L}", loadedListing.ToString())
                .Replace("{V}", loadedViewing.ToString())
                .Replace("{CATEGORY}", category);
            var raw = await vision.AskVisionAsync(images, prompt, 900, ct);
            var parsed = ParseComparison(raw);
            if (parsed is null)
            {
                logger.LogWarning("Porovnání {ListingId}/{Category}: model nevrátil použitelnou odpověď", recordListingId, category);
                continue;
            }

            rows.Add(new ListingPhotoComparison
            {
                ListingId = recordListingId,
                Category = category,
                ListingPhotoCount = loadedListing,
                InspectionPhotoCount = loadedViewing,
                Summary = parsed.Value.Summary,
                Findings = SerializeFindings(parsed.Value.Findings),
            });
        }

        db.ListingPhotoComparisons.AddRange(rows);
        await db.SaveChangesAsync(ct);

        // 4) Zpráva do analýz – je vidět v detailu, v MCP i v RAG
        Guid? analysisId = null;
        if (rows.Count > 0)
        {
            var title = await db.Listings.Where(l => l.Id == recordListingId).Select(l => l.Title).FirstOrDefaultAsync(ct) ?? "";
            var dtos = rows.Select(ToDto).ToList();
            var report = BuildReport(title, dtos, notPhotographed, DateTime.UtcNow);

            var previous = await db.ListingAnalyses
                .Where(a => a.ListingId == recordListingId && a.Source == AnalysisSource)
                .ToListAsync(ct);
            db.ListingAnalyses.RemoveRange(previous);
            await db.SaveChangesAsync(ct);

            var saved = await rag.SaveAnalysisAsync(recordListingId, report, AnalysisSource,
                $"Inzerát vs. prohlídka – porovnání fotek ({DateTime.UtcNow.ToLocalTime():d. M. yyyy})", ct);
            analysisId = saved.Id;
        }

        var findings = rows.Sum(r => DeserializeFindings(r.Findings).Count);
        logger.LogInformation(
            "Porovnání inzerát × prohlídka {ListingId}: {Categories} kategorií, {Findings} nálezů, {Classified} fotek z prohlídky nově klasifikováno",
            recordListingId, rows.Count, findings, classified);

        return new InspectionComparisonResultDto(recordListingId, rows.Count(r => r.ListingPhotoCount > 0), findings,
            classified, notShown, notPhotographed, analysisId,
            rows.Count == 0 ? "Žádnou kategorii se nepodařilo porovnat." : null);
    }

    public async Task<List<PhotoComparisonDto>> GetAsync(Guid listingId, CancellationToken ct)
    {
        var memberIds = await GroupMemberIdsAsync(listingId, ct);
        var rows = await db.ListingPhotoComparisons
            .AsNoTracking()
            .Where(c => memberIds.Contains(c.ListingId))
            .ToListAsync(ct);
        return rows
            .OrderBy(r => Array.IndexOf(ComparableCategories, r.Category))
            .Select(ToDto)
            .ToList();
    }

    public async Task<InspectionFindingsSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var rows = await db.ListingPhotoComparisons
            .AsNoTracking()
            .Select(c => new { c.ListingId, c.Category, c.Findings, c.Listing.Title })
            .ToListAsync(ct);

        var all = rows
            .SelectMany(r => DeserializeFindings(r.Findings).Select(f => (r.ListingId, r.Title, r.Category, Finding: f)))
            .ToList();

        return new InspectionFindingsSummaryDto(
            rows.Select(r => r.ListingId).Distinct().Count(),
            rows.Count,
            all.Count,
            SummarizeByType(all.Select(a => (a.ListingId, a.Finding))),
            all.OrderBy(a => SeverityRank(a.Finding.Severity))
                .ThenBy(a => a.Finding.Type)
                .Take(15)
                .Select(a => new FindingExampleDto(a.ListingId, a.Title, CategoryLabel(a.Category),
                    a.Finding.Type, a.Finding.Severity, a.Finding.Description))
                .ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Nejvýš <paramref name="max"/> položek rovnoměrně přes celý seznam (první vždy).</summary>
    public static List<T> PickEvenly<T>(IReadOnlyList<T> items, int max)
    {
        if (items.Count <= max) return items.ToList();
        var result = new List<T>(max);
        for (var i = 0; i < max; i++)
            result.Add(items[(int)Math.Floor(i * (items.Count / (double)max))]);
        return result;
    }

    /// <summary>Z prohlídky napřed fotky, kde klasifikace viděla vadu (o ty jde), zbytek rovnoměrně.</summary>
    public static List<UserListingPhoto> PickForViewing(IReadOnlyList<UserListingPhoto> photos, int max)
    {
        var damaged = PickEvenly(photos.Where(p => p.DamageDetected).ToList(), max / 2);
        var rest = PickEvenly(photos.Where(p => !damaged.Contains(p)).ToList(), max - damaged.Count);
        return damaged.Concat(rest).ToList();
    }

    /// <summary>Odpověď modelu → (shrnutí, nálezy). Null = nepoužitelná odpověď; jiné místo = bez nálezů.</summary>
    public static (string? Summary, List<PhotoFinding> Findings)? ParseComparison(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()?.Trim()
                : null;
            var samePlace = !root.TryGetProperty("same_place", out var sp) || sp.ValueKind != JsonValueKind.False;
            if (!samePlace)
                return (summary ?? "Fotky z inzerátu a z prohlídky neukazují stejné místo – nešlo porovnat.", []);

            var findings = new List<PhotoFinding>();
            if (root.TryGetProperty("findings", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var description = Text(item, "description");
                    if (string.IsNullOrWhiteSpace(description)) continue;

                    var type = Text(item, "type")?.Trim().ToLowerInvariant() ?? "other";
                    if (!FindingLabels.ContainsKey(type) || type == OmittedType) type = "other";
                    var severity = Text(item, "severity")?.Trim().ToLowerInvariant();
                    if (severity is not ("high" or "medium" or "low")) severity = "low";

                    findings.Add(new PhotoFinding(type, severity, description.Trim(),
                        Number(item, "listing_photo"), Number(item, "viewing_photo")));
                }
            }
            return (summary, findings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<FindingTypeCountDto> SummarizeByType(IEnumerable<(Guid ListingId, PhotoFinding Finding)> findings)
        => findings
            .GroupBy(f => f.Finding.Type)
            .Select(g => new FindingTypeCountDto(g.Key, FindingLabels.GetValueOrDefault(g.Key, g.Key), g.Count(),
                g.Select(f => f.ListingId).Distinct().Count(), g.Count(f => f.Finding.Severity == "high")))
            .OrderByDescending(t => t.Listings).ThenByDescending(t => t.Count)
            .ToList();

    public static string BuildReport(string listingTitle, IReadOnlyList<PhotoComparisonDto> comparisons,
        IReadOnlyList<string> notPhotographedAtViewing, DateTime createdAtUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Inzerát vs. prohlídka – porovnání fotek");
        sb.AppendLine();
        sb.AppendLine($"**{listingTitle}** · vytvořeno {createdAtUtc.ToLocalTime():d. M. yyyy}");
        sb.AppendLine();
        sb.AppendLine("Obrazový model dostal fotky z inzerátu a vaše fotky z prohlídky téže části domu a hledal, v čem se liší. Čísla fotek jsou pořadí ve vzorku, který model viděl, ne v galerii.");
        sb.AppendLine();

        var all = comparisons.SelectMany(c => c.Findings).ToList();
        if (all.Count == 0)
        {
            sb.AppendLine("Ve srovnaných částech domu model nenašel rozdíl mezi inzerátem a skutečností.");
        }
        else
        {
            sb.AppendLine("## Co se liší");
            sb.AppendLine();
            foreach (var type in SummarizeByType(all.Select(f => (Guid.Empty, f))).OrderByDescending(t => t.Count))
                sb.AppendLine($"- **{type.Label}:** {type.Count}×" + (type.High > 0 ? $" (z toho {type.High}× závažné)" : ""));
        }
        sb.AppendLine();

        foreach (var comparison in comparisons)
        {
            sb.AppendLine($"## {comparison.CategoryLabel}");
            sb.AppendLine();
            sb.AppendLine(comparison.ListingPhotoCount > 0
                ? $"Fotek ve vzorku: {comparison.ListingPhotoCount} z inzerátu, {comparison.InspectionPhotoCount} z prohlídky."
                : $"V inzerátu žádná fotka, z prohlídky {comparison.InspectionPhotoCount}.");
            if (!string.IsNullOrWhiteSpace(comparison.Summary))
            {
                sb.AppendLine();
                sb.AppendLine(comparison.Summary);
            }
            if (comparison.Findings.Count > 0)
            {
                sb.AppendLine();
                foreach (var finding in comparison.Findings.OrderBy(f => SeverityRank(f.Severity)))
                {
                    var where = (finding.ListingPhoto, finding.InspectionPhoto) switch
                    {
                        ({ } l, { } v) => $" (inzerát č. {l}, prohlídka č. {v})",
                        ({ } l, null) => $" (inzerát č. {l})",
                        (null, { } v) => $" (prohlídka č. {v})",
                        _ => "",
                    };
                    sb.AppendLine($"- **{SeverityLabel(finding.Severity)} – {FindingLabels.GetValueOrDefault(finding.Type, finding.Type)}:** {finding.Description}{where}");
                }
            }
            sb.AppendLine();
        }

        if (notPhotographedAtViewing.Count > 0)
        {
            sb.AppendLine("## Bez srovnání");
            sb.AppendLine();
            sb.AppendLine($"Na prohlídce jste nevyfotili: {string.Join(", ", notPhotographedAtViewing)}.");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static string CategoryLabel(string category) => CategoryLabels.GetValueOrDefault(category, category);

    private static int SeverityRank(string severity) => severity switch { "high" => 0, "medium" => 1, _ => 2 };

    private static string SeverityLabel(string severity) => severity switch
    {
        "high" => "Závažné", "medium" => "Zavádějící", _ => "Kosmetické",
    };

    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0
            ? n
            : null;

    private static readonly JsonSerializerOptions FindingJson = new(JsonSerializerDefaults.Web);

    private static string SerializeFindings(List<PhotoFinding> findings) => JsonSerializer.Serialize(findings, FindingJson);

    private static List<PhotoFinding> DeserializeFindings(string json)
    {
        try { return JsonSerializer.Deserialize<List<PhotoFinding>>(json, FindingJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private static PhotoComparisonDto ToDto(ListingPhotoComparison row)
        => new(row.Category, CategoryLabel(row.Category), row.ListingPhotoCount, row.InspectionPhotoCount,
            row.Summary, DeserializeFindings(row.Findings), row.CreatedAt);

    private static InspectionComparisonResultDto Empty(Guid listingId, string message, int classified = 0)
        => new(listingId, 0, 0, classified, [], [], null, message);

    // ── přístup k datům ──────────────────────────────────────────────────────

    private async Task<List<Guid>> GroupMemberIdsAsync(Guid listingId, CancellationToken ct)
    {
        var rootId = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == listingId)
            .Select(l => l.DuplicateOfListingId ?? l.Id)
            .FirstOrDefaultAsync(ct);
        if (rootId == Guid.Empty) throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        var ids = await db.Listings
            .AsNoTracking()
            .Where(l => l.Id == rootId || l.DuplicateOfListingId == rootId)
            .Select(l => l.Id)
            .ToListAsync(ct);
        if (!ids.Contains(listingId)) ids.Add(listingId);
        return ids;
    }

    private async Task<List<ListingPhoto>> LoadClassifiedListingPhotosAsync(List<Guid> memberIds, CancellationToken ct)
    {
        var owner = await db.ListingPhotos
            .AsNoTracking()
            .Where(p => memberIds.Contains(p.ListingId) && p.PhotoCategory != null)
            .GroupBy(p => p.ListingId)
            .Select(g => new { ListingId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .FirstOrDefaultAsync(ct);
        if (owner is null) return [];

        return await db.ListingPhotos
            .AsNoTracking()
            .Where(p => p.ListingId == owner.ListingId && p.PhotoCategory != null)
            .OrderBy(p => p.Order)
            .ToListAsync(ct);
    }

    private async Task<byte[]?> LoadListingPhotoAsync(HttpClient http, ListingPhoto photo, CancellationToken ct)
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
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogDebug(ex, "Fotku inzerátu {PhotoId} se nepodařilo načíst", photo.Id);
            return null;
        }
    }
}
