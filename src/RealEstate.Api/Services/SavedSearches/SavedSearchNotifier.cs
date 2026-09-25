using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.SavedSearches;
using RealEstate.Api.Services.Notifications;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.SavedSearches;

/// <summary>
/// Projde aktivní uložená hledání uživatelů s tarifem Hledač+, najde nové inzeráty a zlevnění
/// od posledního běhu a pošle digest e-mailem / Telegramem. Běží po scrapu (endpoint /run)
/// a periodicky z <see cref="SavedSearchHostedService"/>. Nezávisí na ICurrentUser –
/// filtr má vždy UserStatus=null, takže anonymní ListingService vrací totéž jako pro vlastníka.
/// </summary>
public sealed class SavedSearchNotifier(
    RealEstateDbContext db,
    IListingService listings,
    IEmailSender emailSender,
    ITelegramSender telegramSender,
    IConfiguration configuration,
    ILogger<SavedSearchNotifier> logger) : ISavedSearchNotifier
{
    private const string KindNew = "new";
    private const string KindPriceDrop = "price_drop";

    /// <summary>Strop položek jednoho druhu v jednom digestu – zbytek přijde příště.</summary>
    private const int MaxItemsPerRun = 50;

    /// <summary>Strop kandidátů se změnou ceny od posledního běhu (ochrana proti obřímu IN po full rescanu).</summary>
    private const int MaxRecentPriceListings = 5_000;

    private sealed record ListingBrief(Guid Id, string Title, decimal? Price, string LocationText);

    private sealed record Evaluation(
        List<NotificationListingItem> NewListings,
        List<NotificationPriceDropItem> PriceDrops);

    public async Task<SavedSearchRunResultDto> RunAllAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var baseUrl = configuration["APP_PUBLIC_URL"] ?? "http://localhost:5002";

        var searches = await db.SavedSearches
            .Include(s => s.User)
            .Where(s => s.IsActive && s.User.IsActive)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        int evaluated = 0, newTotal = 0, dropTotal = 0, sentTotal = 0, failed = 0;

        foreach (var search in searches)
        {
            ct.ThrowIfCancellationRequested();

            if (!PlanAccess.HasPlan(search.User, UserPlans.Hledac, now))
            {
                logger.LogDebug("Uložené hledání {Id} přeskočeno – uživatel {UserId} nemá tarif Hledač", search.Id, search.UserId);
                continue;
            }

            evaluated++;
            try
            {
                var (newCount, dropCount, sent) = await ProcessSearchAsync(search, now, baseUrl, ct);
                newTotal += newCount;
                dropTotal += dropCount;
                sentTotal += sent;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError(ex, "Vyhodnocení uloženého hledání {Id} „{Name}“ selhalo", search.Id, search.Name);
                db.ChangeTracker.Clear();
            }
        }

        logger.LogInformation(
            "Uložená hledání: vyhodnoceno {Evaluated}, nových {New}, zlevnění {Drops}, doručení {Sent}, chyb {Failed}",
            evaluated, newTotal, dropTotal, sentTotal, failed);

        return new SavedSearchRunResultDto(evaluated, newTotal, dropTotal, sentTotal, failed);
    }

    private async Task<(int NewCount, int DropCount, int Sent)> ProcessSearchAsync(
        SavedSearch search, DateTime now, string baseUrl, CancellationToken ct)
    {
        var since = DateTime.SpecifyKind(search.LastRunAt ?? search.CreatedAt, DateTimeKind.Utc);
        var filter = SavedSearchService.DeserializeFilter(search.FilterJson);

        var evaluation = await EvaluateAsync(search, filter, since, ct);
        var newItems = evaluation.NewListings;
        var drops = evaluation.PriceDrops;

        if (newItems.Count == 0 && drops.Count == 0)
        {
            search.LastRunAt = now;
            await db.SaveChangesAsync(ct);
            return (0, 0, 0);
        }

        var (attempted, sent) = await SendDigestAsync(search, newItems, drops, baseUrl, ct);

        // Zapsat log jen po úspěšném doručení; bez jediného kanálu zapsat také (jinak by se položky hromadily donekonečna).
        var shouldRecord = sent > 0 || attempted == 0;
        if (shouldRecord)
        {
            await RecordNotificationsAsync(search, newItems, drops, now, ct);
            if (attempted == 0)
                logger.LogInformation("Uložené hledání {Id} „{Name}“: {New} nových, {Drops} zlevnění – žádný kanál není k dispozici, jen zapsáno",
                    search.Id, search.Name, newItems.Count, drops.Count);
        }
        else
        {
            logger.LogWarning("Uložené hledání {Id} „{Name}“: doručení selhalo na všech kanálech, zkusí se příště", search.Id, search.Name);
        }

        search.LastRunAt = now;
        if (sent > 0)
        {
            search.LastNotifiedAt = now;
            search.TotalNotified += newItems.Count + drops.Count;
            logger.LogInformation("Uložené hledání {Id} „{Name}“: odesláno {New} nových, {Drops} zlevnění ({Channels} kanál/y)",
                search.Id, search.Name, newItems.Count, drops.Count, sent);
        }

        await SaveNotificationsAsync(ct);
        return (shouldRecord ? newItems.Count : 0, shouldRecord ? drops.Count : 0, sent);
    }

    private async Task<Evaluation> EvaluateAsync(SavedSearch search, Contracts.Listings.ListingFilterDto filter, DateTime since, CancellationToken ct)
    {
        var newItems = new List<NotificationListingItem>();
        var drops = new List<NotificationPriceDropItem>();

        if (search.NotifyNewListings)
        {
            var searchId = search.Id;
            var fresh = await listings.BuildFilteredQuery(filter)
                .Where(l => l.FirstSeenAt > since)
                .Where(l => !db.SavedSearchNotifications.Any(n => n.SavedSearchId == searchId && n.Kind == KindNew && n.ListingId == l.Id))
                .OrderByDescending(l => l.FirstSeenAt)
                .Take(MaxItemsPerRun)
                .Select(l => new ListingBrief(l.Id, l.Title, l.Price, l.LocationText))
                .ToListAsync(ct);

            newItems.AddRange(fresh.Select(l => new NotificationListingItem(l.Id, l.Title, l.Price, l.LocationText)));
        }

        if (search.NotifyPriceDrops)
            drops = await FindPriceDropsAsync(search, filter, since, ct);

        return new Evaluation(newItems, drops);
    }

    private async Task<List<NotificationPriceDropItem>> FindPriceDropsAsync(
        SavedSearch search, Contracts.Listings.ListingFilterDto filter, DateTime since, CancellationToken ct)
    {
        var sinceOffset = new DateTimeOffset(since, TimeSpan.Zero);

        var recentIds = await db.ListingPriceHistories.AsNoTracking()
            .Where(h => h.RecordedAt > sinceOffset)
            .Select(h => h.ListingId)
            .Distinct()
            .Take(MaxRecentPriceListings)
            .ToListAsync(ct);
        if (recentIds.Count == 0) return [];

        var matching = await listings.BuildFilteredQuery(filter)
            .Where(l => recentIds.Contains(l.Id))
            .Select(l => new ListingBrief(l.Id, l.Title, l.Price, l.LocationText))
            .ToListAsync(ct);
        if (matching.Count == 0) return [];

        var matchingIds = matching.Select(m => m.Id).ToList();
        var history = await db.ListingPriceHistories.AsNoTracking()
            .Where(h => matchingIds.Contains(h.ListingId))
            .OrderBy(h => h.RecordedAt)
            .Select(h => new { h.ListingId, h.Price, h.RecordedAt })
            .ToListAsync(ct);

        var alreadyNotified = await db.SavedSearchNotifications.AsNoTracking()
            .Where(n => n.SavedSearchId == search.Id && n.Kind == KindPriceDrop && matchingIds.Contains(n.ListingId))
            .Select(n => new { n.ListingId, n.NewPrice })
            .ToDictionaryAsync(n => n.ListingId, n => n.NewPrice, ct);

        var result = new List<NotificationPriceDropItem>();
        foreach (var group in history.GroupBy(h => h.ListingId))
        {
            var rows = group.Where(h => h.Price is not null).ToList();
            var after = rows.Where(h => h.RecordedAt > sinceOffset).ToList();
            if (after.Count == 0) continue;

            var newPrice = after[^1].Price!.Value;
            var before = rows.LastOrDefault(h => h.RecordedAt <= sinceOffset);
            var oldPrice = before?.Price ?? (after.Count > 1 ? after[0].Price : null);
            if (oldPrice is null || newPrice >= oldPrice.Value) continue;

            if (alreadyNotified.TryGetValue(group.Key, out var notifiedPrice) && notifiedPrice == newPrice)
                continue;

            var brief = matching.First(m => m.Id == group.Key);
            result.Add(new NotificationPriceDropItem(brief.Id, brief.Title, oldPrice.Value, newPrice, brief.LocationText));
            if (result.Count >= MaxItemsPerRun) break;
        }

        return result;
    }

    /// <returns>(počet kanálů, kam se zkoušelo posílat; počet úspěšných)</returns>
    private async Task<(int Attempted, int Sent)> SendDigestAsync(
        SavedSearch search,
        List<NotificationListingItem> newItems,
        List<NotificationPriceDropItem> drops,
        string baseUrl,
        CancellationToken ct)
    {
        var attempted = 0;
        var sent = 0;
        var user = search.User;

        if (search.NotifyEmail && !string.IsNullOrWhiteSpace(user.Email) && emailSender.IsConfigured)
        {
            attempted++;
            try
            {
                await emailSender.SendAsync(
                    user.Email,
                    NotificationMessageBuilder.BuildSubject(search.Name, newItems.Count, drops.Count),
                    NotificationMessageBuilder.BuildEmailHtml(search.Name, newItems, drops, baseUrl),
                    NotificationMessageBuilder.BuildEmailText(search.Name, newItems, drops, baseUrl),
                    ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "E-mail pro uložené hledání {Id} na {Email} selhal", search.Id, user.Email);
            }
        }

        if (search.NotifyTelegram && !string.IsNullOrWhiteSpace(user.TelegramChatId) && telegramSender.IsConfigured)
        {
            attempted++;
            try
            {
                await telegramSender.SendAsync(
                    user.TelegramChatId,
                    NotificationMessageBuilder.BuildTelegramHtml(search.Name, newItems, drops, baseUrl),
                    ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telegram pro uložené hledání {Id} (chat {ChatId}) selhal", search.Id, user.TelegramChatId);
            }
        }

        return (attempted, sent);
    }

    private async Task RecordNotificationsAsync(
        SavedSearch search,
        List<NotificationListingItem> newItems,
        List<NotificationPriceDropItem> drops,
        DateTime now,
        CancellationToken ct)
    {
        foreach (var item in newItems)
        {
            db.SavedSearchNotifications.Add(new SavedSearchNotification
            {
                SavedSearchId = search.Id,
                ListingId = item.ListingId,
                Kind = KindNew,
                NewPrice = item.Price,
                SentAt = now,
            });
        }

        if (drops.Count == 0) return;

        // Unikátní index (search, listing, kind) dovolí jeden řádek na inzerát – další zlevnění ho přepíše.
        var dropIds = drops.Select(d => d.ListingId).ToList();
        var existing = await db.SavedSearchNotifications
            .Where(n => n.SavedSearchId == search.Id && n.Kind == KindPriceDrop && dropIds.Contains(n.ListingId))
            .ToDictionaryAsync(n => n.ListingId, ct);

        foreach (var drop in drops)
        {
            if (existing.TryGetValue(drop.ListingId, out var row))
            {
                row.OldPrice = drop.OldPrice;
                row.NewPrice = drop.NewPrice;
                row.SentAt = now;
                continue;
            }

            db.SavedSearchNotifications.Add(new SavedSearchNotification
            {
                SavedSearchId = search.Id,
                ListingId = drop.ListingId,
                Kind = KindPriceDrop,
                OldPrice = drop.OldPrice,
                NewPrice = drop.NewPrice,
                SentAt = now,
            });
        }
    }

    /// <summary>Souběžný běh (scraper + timer) může narazit na unikátní index – duplicitní řádky se zahodí, stav hledání se uloží.</summary>
    private async Task SaveNotificationsAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Zápis logu upozornění narazil na duplicitu – přidané řádky se zahazují");
            foreach (var entry in db.ChangeTracker.Entries<SavedSearchNotification>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);
        }
    }
}
