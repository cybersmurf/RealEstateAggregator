using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Overview;
using RealEstate.Api.Services.Auth;
using RealEstate.Api.Services.Duplicates;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Overview;

/// <summary>
/// Přehled pro Petra a Lenku. Hlasy, pořadí a prohlídky se vážou na inzerát, ale čtou se za celou
/// skupinu duplicit (dům napříč portály i přeinzerováním); kandidáti = stavy K návštěvě / Líbí se
/// vlastníka workspace. Hlasuje každý pod svým účtem (Lenka je členem Petrova workspace).
/// </summary>
public sealed class OverviewService(
    RealEstateDbContext db,
    ICurrentUser currentUser,
    IListingService listingService,
    IDuplicateGroupService duplicateGroups,
    ILogger<OverviewService> logger) : IOverviewService
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public const decimal BudgetLimit = 7_500_000m;
    public const int NegotiationDays = 60;
    private static readonly string[] CandidateStatuses = ["ToVisit", "Liked"];
    private static readonly string[] Buckets = ["visit", "watch", "second"];
    private static readonly Dictionary<string, string> BucketLabels = new()
    {
        ["visit"] = "Jet na prohlídku",
        ["watch"] = "Sledovat / čekat na slevu",
        ["second"] = "Druhá vlna",
    };

    private Guid OwnerId => currentUser.EffectiveUserId;

    // ─── Denní záznam ─────────────────────────────────────────────────────────

    public async Task<DailyDigestDto?> GetLatestDigestAsync(CancellationToken ct)
    {
        var ownerId = OwnerId;
        var digest = await db.DailyDigests.AsNoTracking()
            .Where(d => d.OwnerId == ownerId)
            .OrderByDescending(d => d.DigestDate)
            .FirstOrDefaultAsync(ct);
        return digest is null ? null : ToDto(digest);
    }

    public async Task<DailyDigestDto> SaveDigestAsync(SaveDigestDto dto, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var date = dto.Date ?? TodayInPrague();
        var digest = await db.DailyDigests.FirstOrDefaultAsync(d => d.OwnerId == ownerId && d.DigestDate == date, ct);
        if (digest is null)
        {
            digest = new DailyDigest { OwnerId = ownerId, DigestDate = date };
            db.DailyDigests.Add(digest);
        }
        digest.Summary = dto.Summary.Trim();
        digest.Items = JsonSerializer.Serialize(dto.Items ?? [], JsonOpts);
        digest.GeneratedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(digest);
    }

    private static DailyDigestDto ToDto(DailyDigest d)
    {
        List<DigestItemDto> items;
        try { items = JsonSerializer.Deserialize<List<DigestItemDto>>(d.Items, JsonOpts) ?? []; }
        catch (JsonException) { items = []; }
        return new DailyDigestDto(d.DigestDate, d.GeneratedAt, d.Summary, items, d.DigestDate == TodayInPrague());
    }

    private static DateOnly TodayInPrague() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Prague));

    // ─── Lidé ─────────────────────────────────────────────────────────────────

    private async Task<List<PersonDto>> LoadPeopleAsync(CancellationToken ct)
    {
        var ownerId = OwnerId;
        var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);
        var memberEmails = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.OwnerId == ownerId).Select(m => m.Email).ToListAsync(ct);
        var members = memberEmails.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => memberEmails.Contains(u.Email)).OrderBy(u => u.CreatedAt).ToListAsync(ct);

        var people = new List<PersonDto>();
        if (owner is not null) people.Add(ToPerson(owner));
        people.AddRange(members.Where(m => m.Id != ownerId).Select(ToPerson));
        return people;

        PersonDto ToPerson(User u)
        {
            var isMe = currentUser.UserId == u.Id;
            var name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email.Split('@')[0] : u.DisplayName!;
            return new PersonDto(u.Id, name, isMe, isMe && (currentUser.CanWriteWorkspace || currentUser.IsAdmin));
        }
    }

    // ─── Skupiny duplicit ─────────────────────────────────────────────────────

    /// <summary>Pro každý kořen skupiny všechny id členů (kořen + duplikáty, aktivní i stažené).</summary>
    private async Task<Dictionary<Guid, List<Guid>>> LoadGroupMembersAsync(IReadOnlyCollection<Guid> rootIds, CancellationToken ct)
    {
        var rows = await db.Listings.AsNoTracking()
            .Where(l => rootIds.Contains(l.Id) || (l.DuplicateOfListingId != null && rootIds.Contains(l.DuplicateOfListingId.Value)))
            .Select(l => new { l.Id, Root = l.DuplicateOfListingId ?? l.Id })
            .ToListAsync(ct);
        var result = rootIds.ToDictionary(r => r, _ => new List<Guid>());
        foreach (var row in rows)
            if (result.TryGetValue(row.Root, out var list)) list.Add(row.Id);
        return result;
    }

    private async Task<Guid> RootOfAsync(Guid listingId, CancellationToken ct)
    {
        var root = await db.Listings.AsNoTracking().Where(l => l.Id == listingId)
            .Select(l => (Guid?)(l.DuplicateOfListingId ?? l.Id)).FirstOrDefaultAsync(ct);
        return root ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");
    }

    // ─── Kandidáti ────────────────────────────────────────────────────────────

    public async Task<CandidatesDto> GetCandidatesAsync(CancellationToken ct)
    {
        var ownerId = OwnerId;
        var people = await LoadPeopleAsync(ct);

        // Stav patří domu: vezmeme každou kopii se stavem K návštěvě / Líbí se a dům zastoupíme
        // živou kopií skupiny (nebo tou se stavem, když už žádná neběží).
        var tagged = await db.UserListingStates.AsNoTracking()
            .Where(s => s.UserId == ownerId && CandidateStatuses.Contains(s.Status))
            .Select(s => new { s.ListingId, s.Status, s.Notes, s.LastUpdated, Root = s.Listing.DuplicateOfListingId ?? s.Listing.Id })
            .ToListAsync(ct);
        if (tagged.Count == 0)
            return new CandidatesDto(people, Buckets.Select(b => new BucketDto(b, BucketLabels[b], [])).ToList(), DateTime.UtcNow);

        var rootIds = tagged.Select(t => t.Root).Distinct().ToList();
        var members = await LoadGroupMembersAsync(rootIds, ct);
        var allMemberIds = members.Values.SelectMany(v => v).ToList();

        var listings = await db.Listings.AsNoTracking()
            .Include(l => l.Photos)
            .Where(l => allMemberIds.Contains(l.Id))
            .ToListAsync(ct);
        var byId = listings.ToDictionary(l => l.Id);

        var votes = await db.HouseVotes.AsNoTracking().Include(v => v.User)
            .Where(v => allMemberIds.Contains(v.ListingId)).ToListAsync(ct);
        var ranks = await db.HouseRanks.AsNoTracking()
            .Where(r => r.OwnerId == ownerId && allMemberIds.Contains(r.ListingId)).ToListAsync(ct);
        var visits = await db.HouseVisits.AsNoTracking()
            .Where(v => v.OwnerId == ownerId && allMemberIds.Contains(v.ListingId) && v.Status != "done" && v.Status != "cancelled")
            .ToListAsync(ct);
        var analyses = await db.ListingAnalyses.AsNoTracking()
            .Where(a => allMemberIds.Contains(a.ListingId) && a.Source != "auto")
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.ListingId, a.Title, a.CreatedAt, a.Structured })
            .ToListAsync(ct);

        var cards = new List<HouseCardDto>();
        foreach (var root in rootIds)
        {
            var ids = members[root];
            var group = ids.Select(id => byId.GetValueOrDefault(id)).Where(l => l is not null).Select(l => l!).ToList();
            if (group.Count == 0) continue;
            var state = tagged.Where(t => t.Root == root).OrderByDescending(t => t.LastUpdated).First();
            var shown = group.FirstOrDefault(l => l.IsActive && l.Id == root)
                        ?? group.Where(l => l.IsActive).OrderBy(l => l.FirstSeenAt).FirstOrDefault()
                        ?? group.FirstOrDefault(l => l.Id == state.ListingId)
                        ?? group[0];

            var card = await BuildCardAsync(shown, group, ct);
            card.GroupId = root;
            card.UserStatus = state.Status;
            card.Notes = state.Notes;

            var structured = analyses.FirstOrDefault(a => ids.Contains(a.ListingId) && a.Structured != null)
                             ?? analyses.FirstOrDefault(a => ids.Contains(a.ListingId));
            if (structured is not null)
            {
                card.LastAnalysisId = structured.Id;
                card.LastAnalysisAt = structured.CreatedAt;
                card.LastAnalysisTitle = structured.Title;
                ApplyStructured(card, structured.Structured);
            }

            card.Votes = ToVoteDtos(votes.Where(v => ids.Contains(v.ListingId)), people, card.Price);
            card.Agreed = people.Count > 0 && people.All(p => card.Votes.Any(v => v.UserId == p.UserId && v.Value == "go"));
            card.Vetoed = card.Votes.Any(v => v.Value == "no");

            var rank = ranks.Where(r => ids.Contains(r.ListingId)).OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            card.Bucket = rank?.Bucket ?? DefaultBucket(card);
            card.Position = rank?.Position ?? int.MaxValue;

            var visit = visits.Where(v => ids.Contains(v.ListingId)).OrderBy(v => v.ScheduledAt ?? DateTime.MaxValue).FirstOrDefault();
            card.UpcomingVisitId = visit?.Id;
            card.UpcomingVisitAt = visit?.ScheduledAt;

            cards.Add(card);
        }

        var buckets = Buckets.Select(b => new BucketDto(b, BucketLabels[b],
            cards.Where(c => c.Bucket == b)
                .OrderBy(c => c.Position)
                .ThenBy(c => c.IsActive ? 0 : 1)
                .ThenByDescending(c => c.Agreed)
                .ThenByDescending(c => c.CriteriaMet)
                .ThenBy(c => c.Price ?? decimal.MaxValue)
                .ToList())).ToList();
        return new CandidatesDto(people, buckets, DateTime.UtcNow);
    }

    private static string DefaultBucket(HouseCardDto card)
    {
        if (card.UserStatus == "ToVisit") return "visit";
        if (card.WaitForDiscount) return "watch";
        return card.Match == "fits" ? "watch" : "second";
    }

    private async Task<HouseCardDto> BuildCardAsync(Listing shown, IReadOnlyList<Listing> group, CancellationToken ct)
    {
        var photoSet = await duplicateGroups.GetGroupPhotoSetAsync(shown.Id, ct);
        var photo = photoSet.Photos.OrderBy(p => p.Order)
            .Select(p => string.IsNullOrWhiteSpace(p.OriginalUrl) ? p.StoredUrl : p.OriginalUrl)
            .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

        var card = new HouseCardDto
        {
            ListingId = shown.Id,
            Title = shown.Title,
            Municipality = shown.Municipality,
            LocationText = shown.LocationText,
            PhotoUrl = photo,
            Url = shown.Url,
            SourceCode = shown.SourceCode,
            IsActive = shown.IsActive,
            Price = shown.Price,
        };

        // Cena a dny na trhu za celý dům (všechny kopie i přeinzerování)
        var history = await listingService.GetPriceHistoryAsync(shown.Id, ct) ?? [];
        var priced = history.Where(h => h.Price is > 0).ToList();
        if (priced.Count > 0)
        {
            card.OriginalPrice = priced[0].Price;
            var last = priced[^1];
            if (card.Price is null) card.Price = last.Price;
            if (card.OriginalPrice is > 0 && card.Price is not null)
                card.PriceChangePct = Math.Round((double)((card.Price.Value - card.OriginalPrice.Value) / card.OriginalPrice.Value) * 100, 1);
            for (var i = priced.Count - 1; i > 0; i--)
                if (priced[i].Price != priced[i - 1].Price) { card.LastPriceChangeAt = priced[i].RecordedAt.UtcDateTime; break; }
        }
        var firstSeen = group.Min(l => l.FirstSeenAt);
        card.DaysOnMarket = ListingService.DaysOnMarket(shown, firstSeen);
        var hadDrop = priced.Zip(priced.Skip(1)).Any(p => p.Second.Price < p.First.Price);
        card.NegotiationRoom = shown.IsActive && card.DaysOnMarket >= NegotiationDays && !hadDrop;
        card.WaitForDiscount = card.Price is > BudgetLimit;

        var target = TargetProfile.Evaluate(shown);
        card.CriteriaMet = target.Met;
        card.CriteriaTotal = target.Total;
        card.CriteriaMissing = target.Missing;
        card.Match = target.Met == target.Total ? "fits" : target.Met >= 5 ? "partial" : "no";
        card.HouseType = HouseTypeLabel(shown);
        return card;
    }

    private static string? HouseTypeLabel(Listing l)
    {
        var position = HousePositions.Label(l.HousePosition);
        var singleFloor = JsonFlag(l.AiNormalizedData, "is_single_floor");
        var floors = singleFloor == true ? "bungalov" : singleFloor == false ? "patrový" : null;
        return (floors, position) switch
        {
            (null, null) => null,
            (null, _) => position,
            (_, null) => floors,
            _ => $"{floors}, {position}",
        };
    }

    private static bool? JsonFlag(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var v)) return null;
            return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Strukturované hodnocení z analýzy (uložené přes MCP save_analysis(structured=…)) → karta.</summary>
    public static void ApplyStructured(HouseCardDto card, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? Str(params string[] names)
            {
                foreach (var n in names)
                    if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        return v.GetString()!.Trim();
                return null;
            }
            List<string> Arr(params string[] names)
            {
                foreach (var n in names)
                    if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array)
                        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).ToList();
                return [];
            }

            var shoda = Str("shoda", "match")?.ToLowerInvariant();
            if (shoda is not null)
                card.Match = shoda.Contains("nesed") || shoda == "no" ? "no"
                    : shoda.Contains("část") || shoda.Contains("cast") || shoda == "partial" ? "partial"
                    : "fits";
            card.HouseType = Str("typ", "type") ?? card.HouseType;
            card.HabitableFloor = Str("obyvatelne_podlazi", "habitable_floor");
            card.Verdict = Str("verdikt", "verdict");
            card.Warnings = Arr("varovani", "warnings");
            card.Pluses = Arr("plusy", "pluses");
            card.Minuses = Arr("minusy", "minuses");
            card.VerifyOnSite = Arr("overit_na_miste", "verify_on_site");
            card.Variant = Str("varianta", "variant");
            card.Trainer = Str("trenazer", "trainer");
            card.Heating = Str("topeni", "heating");
            card.RoomsNote = Str("pokoje", "rooms");
            card.ExtraSpace = Str("prostor_navic", "extra_space");
            card.Garage = Str("garaz", "garage");
            if (root.TryGetProperty("zahrada_m2", out var g) && g.ValueKind == JsonValueKind.Number)
                card.GardenM2 = g.GetDouble();
        }
    }

    // ─── Hlasy ────────────────────────────────────────────────────────────────

    private static List<VoteDto> ToVoteDtos(IEnumerable<HouseVote> votes, IReadOnlyList<PersonDto> people, decimal? currentPrice)
    {
        var result = new List<VoteDto>();
        foreach (var byUser in votes.GroupBy(v => v.UserId))
        {
            var v = byUser.OrderByDescending(x => x.UpdatedAt).First();
            var name = people.FirstOrDefault(p => p.UserId == v.UserId)?.Name
                       ?? (string.IsNullOrWhiteSpace(v.User?.DisplayName) ? v.User?.Email?.Split('@')[0] ?? "?" : v.User!.DisplayName!);
            var before = v.PriceAtVote is not null && currentPrice is not null && v.PriceAtVote != currentPrice;
            result.Add(new VoteDto(v.UserId, name, v.Value, v.Note, v.UpdatedAt, before));
        }
        return result.OrderBy(v => people.ToList().FindIndex(p => p.UserId == v.UserId)).ToList();
    }

    public async Task<List<VoteDto>> GetVotesAsync(Guid listingId, CancellationToken ct)
    {
        var root = await RootOfAsync(listingId, ct);
        var members = (await LoadGroupMembersAsync([root], ct))[root];
        var people = await LoadPeopleAsync(ct);
        var price = await db.Listings.AsNoTracking().Where(l => l.Id == listingId).Select(l => l.Price).FirstOrDefaultAsync(ct);
        var votes = await db.HouseVotes.AsNoTracking().Include(v => v.User).Where(v => members.Contains(v.ListingId)).ToListAsync(ct);
        return ToVoteDtos(votes, people, price);
    }

    public async Task<Dictionary<Guid, List<VoteDto>>> GetAllVotesAsync(CancellationToken ct)
    {
        var people = await LoadPeopleAsync(ct);
        var peopleIds = people.Select(p => p.UserId).ToList();
        var votes = await db.HouseVotes.AsNoTracking().Include(v => v.User).Include(v => v.Listing)
            .Where(v => peopleIds.Contains(v.UserId)).ToListAsync(ct);
        var result = new Dictionary<Guid, List<VoteDto>>();
        foreach (var byRoot in votes.GroupBy(v => v.Listing.DuplicateOfListingId ?? v.ListingId))
            result[byRoot.Key] = ToVoteDtos(byRoot, people, byRoot.First().Listing.Price);
        return result;
    }

    public async Task<List<VoteDto>> VoteAsync(Guid listingId, VoteRequestDto request, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedAccessException("Hlasovat může jen přihlášený uživatel");
        var value = (request.Value ?? "").Trim().ToLowerInvariant();
        if (value is not ("go" or "maybe" or "no"))
            throw new ArgumentException("Hlas musí být go, maybe nebo no");

        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");
        var root = listing.DuplicateOfListingId ?? listing.Id;
        var members = (await LoadGroupMembersAsync([root], ct))[root];

        // Jeden hlas na osobu a dům: starší hlas u jiné kopie téhož domu se přepíše
        var existing = await db.HouseVotes.Where(v => v.UserId == userId && members.Contains(v.ListingId)).ToListAsync(ct);
        var vote = existing.OrderByDescending(v => v.UpdatedAt).FirstOrDefault();
        if (vote is null)
        {
            vote = new HouseVote { ListingId = listingId, UserId = userId };
            db.HouseVotes.Add(vote);
        }
        else
        {
            db.HouseVotes.RemoveRange(existing.Where(v => v.Id != vote.Id));
            vote.ListingId = listingId;
        }
        vote.Value = value;
        vote.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        vote.PriceAtVote = listing.Price;
        vote.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Hlas {Value} od {User} k domu {Listing}", value, userId, listingId);
        return await GetVotesAsync(listingId, ct);
    }

    public async Task<bool> ClearVoteAsync(Guid listingId, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedAccessException("Hlasovat může jen přihlášený uživatel");
        var root = await RootOfAsync(listingId, ct);
        var members = (await LoadGroupMembersAsync([root], ct))[root];
        var deleted = await db.HouseVotes.Where(v => v.UserId == userId && members.Contains(v.ListingId)).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    // ─── Pořadí ───────────────────────────────────────────────────────────────

    public async Task SaveRanksAsync(IReadOnlyList<RankItemDto> items, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var ids = items.Select(i => i.ListingId).Distinct().ToList();
        var roots = await db.Listings.AsNoTracking().Where(l => ids.Contains(l.Id))
            .Select(l => new { l.Id, Root = l.DuplicateOfListingId ?? l.Id }).ToDictionaryAsync(x => x.Id, x => x.Root, ct);
        var members = await LoadGroupMembersAsync(roots.Values.Distinct().ToList(), ct);
        var allMemberIds = members.Values.SelectMany(v => v).ToList();
        var existing = await db.HouseRanks.Where(r => r.OwnerId == ownerId && allMemberIds.Contains(r.ListingId)).ToListAsync(ct);

        foreach (var item in items)
        {
            if (!roots.TryGetValue(item.ListingId, out var root)) continue;
            var bucket = Buckets.Contains(item.Bucket) ? item.Bucket : "watch";
            var groupIds = members[root];
            var rank = existing.Where(r => groupIds.Contains(r.ListingId)).OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            if (rank is null)
            {
                rank = new HouseRank { OwnerId = ownerId, ListingId = item.ListingId };
                db.HouseRanks.Add(rank);
                existing.Add(rank);
            }
            rank.ListingId = item.ListingId;
            rank.Bucket = bucket;
            rank.Position = item.Position;
            rank.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    // ─── Prohlídky ────────────────────────────────────────────────────────────

    public async Task<List<VisitDto>> GetVisitsAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var query = db.HouseVisits.AsNoTracking().Include(v => v.Listing).ThenInclude(l => l.Photos)
            .Where(v => v.OwnerId == ownerId);
        if (from is { } f) query = query.Where(v => v.ScheduledAt == null || v.ScheduledAt >= f.ToUniversalTime());
        if (to is { } t) query = query.Where(v => v.ScheduledAt == null || v.ScheduledAt <= t.ToUniversalTime());
        var visits = await query.OrderBy(v => v.Status == "done" || v.Status == "cancelled")
            .ThenBy(v => v.ScheduledAt ?? DateTime.MaxValue).ToListAsync(ct);
        return visits.Select(ToDto).ToList();
    }

    public async Task<VisitDto?> GetVisitAsync(Guid id, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var visit = await db.HouseVisits.AsNoTracking().Include(v => v.Listing).ThenInclude(l => l.Photos)
            .FirstOrDefaultAsync(v => v.Id == id && v.OwnerId == ownerId, ct);
        return visit is null ? null : ToDto(visit);
    }

    public async Task<VisitDto> CreateVisitAsync(VisitUpsertDto dto, CancellationToken ct)
    {
        var listingId = dto.ListingId ?? throw new ArgumentException("Chybí listingId");
        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct)
                      ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        var checklist = dto.Checklist;
        if (checklist is null || checklist.Count == 0)
            checklist = await DefaultChecklistAsync(listing, ct);

        var visit = new HouseVisit
        {
            ListingId = listingId,
            OwnerId = OwnerId,
            ScheduledAt = dto.ScheduledAt?.ToUniversalTime(),
            AgentName = Trim(dto.AgentName) ?? listing.SellerName,
            AgentPhone = Trim(dto.AgentPhone) ?? listing.SellerPhone,
            Address = Trim(dto.Address) ?? listing.LocationText,
            Checklist = JsonSerializer.Serialize(checklist, JsonOpts),
            Note = Trim(dto.Note),
            Status = NormalizeVisitStatus(dto.Status) ?? "planned",
            CreatedByUserId = currentUser.UserId,
        };
        db.HouseVisits.Add(visit);
        await db.SaveChangesAsync(ct);
        return (await GetVisitAsync(visit.Id, ct))!;
    }

    public async Task<VisitDto?> UpdateVisitAsync(Guid id, VisitUpsertDto dto, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var visit = await db.HouseVisits.FirstOrDefaultAsync(v => v.Id == id && v.OwnerId == ownerId, ct);
        if (visit is null) return null;
        if (dto.ScheduledAt is not null) visit.ScheduledAt = dto.ScheduledAt.Value.ToUniversalTime();
        if (dto.AgentName is not null) visit.AgentName = Trim(dto.AgentName);
        if (dto.AgentPhone is not null) visit.AgentPhone = Trim(dto.AgentPhone);
        if (dto.Address is not null) visit.Address = Trim(dto.Address);
        if (dto.Checklist is not null) visit.Checklist = JsonSerializer.Serialize(dto.Checklist, JsonOpts);
        if (dto.Note is not null) visit.Note = Trim(dto.Note);
        if (NormalizeVisitStatus(dto.Status) is { } status) visit.Status = status;
        visit.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetVisitAsync(id, ct);
    }

    /// <summary>„Hotovo": prohlídka done a dům ve stavu Navštíveno (poznámka k domu se nemění, poznámka z prohlídky zůstává u prohlídky).</summary>
    public async Task<VisitDto?> CompleteVisitAsync(Guid id, string? note, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var visit = await db.HouseVisits.FirstOrDefaultAsync(v => v.Id == id && v.OwnerId == ownerId, ct);
        if (visit is null) return null;
        visit.Status = "done";
        if (!string.IsNullOrWhiteSpace(note)) visit.Note = note.Trim();
        visit.UpdatedAt = DateTime.UtcNow;

        var state = await db.UserListingStates.FirstOrDefaultAsync(s => s.ListingId == visit.ListingId && s.UserId == ownerId, ct);
        if (state is null)
            db.UserListingStates.Add(new UserListingState { Id = Guid.NewGuid(), ListingId = visit.ListingId, UserId = ownerId, Status = "Visited", LastUpdated = DateTime.UtcNow });
        else
        {
            state.Status = "Visited";
            state.LastUpdated = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return await GetVisitAsync(id, ct);
    }

    public async Task<bool> DeleteVisitAsync(Guid id, CancellationToken ct)
    {
        var ownerId = OwnerId;
        var deleted = await db.HouseVisits.Where(v => v.Id == id && v.OwnerId == ownerId).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    /// <summary>Checklist z poslední analýzy domu (strukturované pole overit_na_miste) za celou skupinu duplicit.</summary>
    private async Task<List<ChecklistItemDto>> DefaultChecklistAsync(Listing listing, CancellationToken ct)
    {
        var root = listing.DuplicateOfListingId ?? listing.Id;
        var members = (await LoadGroupMembersAsync([root], ct))[root];
        var structured = await db.ListingAnalyses.AsNoTracking()
            .Where(a => members.Contains(a.ListingId) && a.Structured != null)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.Structured)
            .FirstOrDefaultAsync(ct);
        var card = new HouseCardDto();
        ApplyStructured(card, structured);
        var items = card.VerifyOnSite.Select(t => new ChecklistItemDto(t, false)).ToList();
        items.AddRange(card.Warnings.Where(w => !card.VerifyOnSite.Contains(w)).Select(w => new ChecklistItemDto($"Ověřit: {w}", false)));
        return items;
    }

    private static VisitDto ToDto(HouseVisit v)
    {
        List<ChecklistItemDto> checklist;
        try { checklist = JsonSerializer.Deserialize<List<ChecklistItemDto>>(v.Checklist, JsonOpts) ?? []; }
        catch (JsonException) { checklist = []; }
        var l = v.Listing;
        return new VisitDto
        {
            Id = v.Id,
            ListingId = v.ListingId,
            Title = l?.Title ?? "",
            Municipality = l?.Municipality,
            LocationText = l?.LocationText,
            PhotoUrl = l?.Photos.OrderBy(p => p.Order).Select(p => string.IsNullOrWhiteSpace(p.OriginalUrl) ? p.StoredUrl : p.OriginalUrl).FirstOrDefault(),
            Url = l?.Url,
            Price = l?.Price,
            ScheduledAt = v.ScheduledAt,
            AgentName = v.AgentName,
            AgentPhone = v.AgentPhone,
            Address = v.Address,
            Checklist = checklist,
            Note = v.Note,
            Status = v.Status,
            UpdatedAt = v.UpdatedAt,
        };
    }

    private static string? NormalizeVisitStatus(string? status)
    {
        var s = status?.Trim().ToLowerInvariant();
        return s is "proposed" or "planned" or "done" or "cancelled" ? s : null;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
