using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Contracts.SavedSearches;
using RealEstate.Api.Services.Auth;
using RealEstate.Api.Services.Notifications;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.SavedSearches;

public sealed class SavedSearchService(
    RealEstateDbContext db,
    ICurrentUser currentUser,
    IListingService listings,
    IEmailSender emailSender,
    ITelegramSender telegramSender,
    IConfiguration configuration,
    ILogger<SavedSearchService> logger) : ISavedSearchService
{
    private const int MaxNameLength = 200;
    private const int NotificationsTake = 50;

    public static readonly JsonSerializerOptions FilterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private Guid UserId => currentUser.UserId ?? throw new InvalidOperationException("Uložená hledání vyžadují přihlášení.");

    public async Task<List<SavedSearchDto>> ListAsync(CancellationToken ct)
    {
        var entities = await db.SavedSearches.AsNoTracking()
            .Where(s => s.UserId == UserId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

        var result = new List<SavedSearchDto>(entities.Count);
        foreach (var entity in entities)
        {
            var filter = DeserializeFilter(entity.FilterJson);
            int? count = null;
            try
            {
                count = await listings.BuildFilteredQuery(filter).CountAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Počet shod uloženého hledání {Id} se nepodařilo spočítat", entity.Id);
            }
            result.Add(ToDto(entity, filter, count));
        }
        return result;
    }

    public async Task<SavedSearchDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.SavedSearches.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (entity is null) return null;

        var filter = DeserializeFilter(entity.FilterJson);
        var count = await listings.BuildFilteredQuery(filter).CountAsync(ct);
        return ToDto(entity, filter, count);
    }

    public async Task<(SavedSearchDto? Result, string? Error)> CreateAsync(SavedSearchUpsertDto request, CancellationToken ct)
    {
        var validation = Validate(request);
        if (validation is not null) return (null, validation);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        if (user is null) return (null, "Uživatel nebyl nalezen.");

        var limit = PlanAccess.MaxSavedSearches(user);
        if (limit is { } max)
        {
            var current = await db.SavedSearches.CountAsync(s => s.UserId == UserId, ct);
            if (current >= max)
                return (null, $"Váš tarif umožňuje nejvýše {max} uložených hledání. Smažte některé nebo přejděte na vyšší tarif.");
        }

        var entity = new SavedSearch
        {
            UserId = UserId,
            Name = request.Name.Trim(),
            FilterJson = SerializeFilter(request.Filter),
            NotifyEmail = request.NotifyEmail,
            NotifyTelegram = request.NotifyTelegram,
            NotifyNewListings = request.NotifyNewListings,
            NotifyPriceDrops = request.NotifyPriceDrops,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow,
        };
        db.SavedSearches.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Uživatel {UserId} založil uložené hledání {Id} „{Name}“", UserId, entity.Id, entity.Name);
        var filter = DeserializeFilter(entity.FilterJson);
        var count = await listings.BuildFilteredQuery(filter).CountAsync(ct);
        return (ToDto(entity, filter, count), null);
    }

    public async Task<(SavedSearchDto? Result, string? Error)> UpdateAsync(Guid id, SavedSearchUpsertDto request, CancellationToken ct)
    {
        var validation = Validate(request);
        if (validation is not null) return (null, validation);

        var entity = await db.SavedSearches.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (entity is null) return (null, null); // 404 – nic k validaci

        entity.Name = request.Name.Trim();
        entity.FilterJson = SerializeFilter(request.Filter);
        entity.NotifyEmail = request.NotifyEmail;
        entity.NotifyTelegram = request.NotifyTelegram;
        entity.NotifyNewListings = request.NotifyNewListings;
        entity.NotifyPriceDrops = request.NotifyPriceDrops;
        entity.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);

        var filter = DeserializeFilter(entity.FilterJson);
        var count = await listings.BuildFilteredQuery(filter).CountAsync(ct);
        return (ToDto(entity, filter, count), null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.SavedSearches.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (entity is null) return false;

        db.SavedSearches.Remove(entity);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Uživatel {UserId} smazal uložené hledání {Id}", UserId, id);
        return true;
    }

    public async Task<List<SavedSearchNotificationDto>?> ListNotificationsAsync(Guid id, CancellationToken ct)
    {
        var exists = await db.SavedSearches.AsNoTracking().AnyAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (!exists) return null;

        return await db.SavedSearchNotifications.AsNoTracking()
            .Where(n => n.SavedSearchId == id)
            .OrderByDescending(n => n.SentAt)
            .Take(NotificationsTake)
            .Select(n => new SavedSearchNotificationDto(
                n.ListingId,
                n.Kind,
                n.OldPrice,
                n.NewPrice,
                n.SentAt,
                db.Listings.Where(l => l.Id == n.ListingId).Select(l => l.Title).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<SavedSearchTestResultDto?> SendTestAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.SavedSearches.AsNoTracking()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId, ct);
        if (entity is null) return null;

        var baseUrl = configuration["APP_PUBLIC_URL"] ?? "http://localhost:5002";
        var emailSent = false;
        var telegramSent = false;
        string? emailError = null;
        string? telegramError = null;

        if (!entity.NotifyEmail)
            emailError = "E-mail není u tohoto hledání zapnutý.";
        else if (string.IsNullOrWhiteSpace(entity.User.Email))
            emailError = "Účet nemá e-mail.";
        else if (!emailSender.IsConfigured)
            emailError = "Server nemá nastavené SMTP.";
        else
        {
            try
            {
                await emailSender.SendAsync(
                    entity.User.Email,
                    $"Zkušební zpráva: {entity.Name}",
                    NotificationMessageBuilder.BuildTestEmailHtml(entity.Name, baseUrl),
                    NotificationMessageBuilder.BuildTestEmailText(entity.Name, baseUrl),
                    ct);
                emailSent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Zkušební e-mail pro hledání {Id} selhal", id);
                emailError = ex.Message;
            }
        }

        if (!entity.NotifyTelegram)
            telegramError = "Telegram není u tohoto hledání zapnutý.";
        else if (string.IsNullOrWhiteSpace(entity.User.TelegramChatId))
            telegramError = "Účet nemá propojený Telegram (chybí chat ID).";
        else if (!telegramSender.IsConfigured)
            telegramError = "Server nemá nastavený Telegram bot.";
        else
        {
            try
            {
                await telegramSender.SendAsync(
                    entity.User.TelegramChatId,
                    NotificationMessageBuilder.BuildTestTelegramHtml(entity.Name, baseUrl),
                    ct);
                telegramSent = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Zkušební Telegram zpráva pro hledání {Id} selhala", id);
                telegramError = ex.Message;
            }
        }

        return new SavedSearchTestResultDto(emailSent, telegramSent, emailError, telegramError);
    }

    // ── Pomocné ─────────────────────────────────────────────────────────────

    private static string? Validate(SavedSearchUpsertDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "Název hledání je povinný.";
        if (request.Name.Trim().Length > MaxNameLength)
            return $"Název hledání může mít nejvýše {MaxNameLength} znaků.";
        if (request.Filter is null)
            return "Filtr hledání chybí.";
        return null;
    }

    /// <summary>Uloží filtr normalizovaný: bez stránkování a bez osobního stavu (v jobu není uživatel).</summary>
    public static string SerializeFilter(ListingFilterDto filter)
    {
        filter.Page = 1;
        filter.PageSize = 1;
        filter.UserStatus = null;
        filter.OnlyNewSince = null;
        return JsonSerializer.Serialize(filter, FilterJsonOptions);
    }

    public static ListingFilterDto DeserializeFilter(string json)
    {
        ListingFilterDto? filter = null;
        try
        {
            filter = JsonSerializer.Deserialize<ListingFilterDto>(json, FilterJsonOptions);
        }
        catch (JsonException)
        {
            // Poškozený filtr – hledání se chová jako „vše“, uživatel ho může upravit
        }
        filter ??= new ListingFilterDto();
        filter.Page = 1;
        filter.PageSize = 1;
        filter.UserStatus = null;
        filter.OnlyNewSince = null;
        return filter;
    }

    private static SavedSearchDto ToDto(SavedSearch s, ListingFilterDto filter, int? count) => new(
        s.Id, s.Name, filter,
        s.NotifyEmail, s.NotifyTelegram, s.NotifyNewListings, s.NotifyPriceDrops, s.IsActive,
        s.CreatedAt, s.LastRunAt, s.LastNotifiedAt, s.TotalNotified, count);
}
