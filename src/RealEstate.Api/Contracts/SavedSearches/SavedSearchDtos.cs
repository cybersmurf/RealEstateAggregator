using RealEstate.Api.Contracts.Listings;

namespace RealEstate.Api.Contracts.SavedSearches;

/// <summary>Uložené hledání vlastníka včetně aktuálního počtu shod (spočítáno při výpisu).</summary>
public sealed record SavedSearchDto(
    Guid Id,
    string Name,
    ListingFilterDto Filter,
    bool NotifyEmail,
    bool NotifyTelegram,
    bool NotifyNewListings,
    bool NotifyPriceDrops,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? LastRunAt,
    DateTime? LastNotifiedAt,
    int TotalNotified,
    int? CurrentMatchCount);

/// <summary>Vstup pro založení i úpravu uloženého hledání.</summary>
public sealed record SavedSearchUpsertDto(
    string Name,
    ListingFilterDto Filter,
    bool NotifyEmail = true,
    bool NotifyTelegram = false,
    bool NotifyNewListings = true,
    bool NotifyPriceDrops = true,
    bool IsActive = true);

/// <summary>Souhrn jednoho běhu vyhodnocení všech uložených hledání.</summary>
public sealed record SavedSearchRunResultDto(
    int SearchesEvaluated,
    int NewListings,
    int PriceDrops,
    int NotificationsSent,
    int Failed);

/// <summary>Položka historie odeslaných upozornění.</summary>
public sealed record SavedSearchNotificationDto(
    Guid ListingId,
    string Kind,
    decimal? OldPrice,
    decimal? NewPrice,
    DateTime SentAt,
    string? ListingTitle);

/// <summary>Výsledek zkušebního doručení – které kanály se povedly a proč ne.</summary>
public sealed record SavedSearchTestResultDto(
    bool EmailSent,
    bool TelegramSent,
    string? EmailError,
    string? TelegramError);
