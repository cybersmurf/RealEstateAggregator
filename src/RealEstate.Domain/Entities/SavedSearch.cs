namespace RealEstate.Domain.Entities;

/// <summary>
/// Uložené hledání uživatele. Filtr je serializovaný ListingFilterDto (jsonb),
/// po každém scrapu se vyhodnotí nové inzeráty a zlevnění a pošlou se upozornění.
/// </summary>
public sealed class SavedSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = null!;
    /// <summary>JSON ListingFilterDto (jsonb).</summary>
    public string FilterJson { get; set; } = "{}";
    public bool NotifyEmail { get; set; } = true;
    public bool NotifyTelegram { get; set; }
    public bool NotifyNewListings { get; set; } = true;
    public bool NotifyPriceDrops { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Kdy job hledání naposledy vyhodnotil – nové inzeráty se berou od tohoto času.</summary>
    public DateTime? LastRunAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public int TotalNotified { get; set; }

    public User User { get; set; } = null!;
    public ICollection<SavedSearchNotification> Notifications { get; set; } = new List<SavedSearchNotification>();
}

/// <summary>Log odeslaných upozornění – brání opakovanému odeslání téhož inzerátu.</summary>
public sealed class SavedSearchNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SavedSearchId { get; set; }
    public Guid ListingId { get; set; }
    /// <summary>"new" | "price_drop"</summary>
    public string Kind { get; set; } = "new";
    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    public SavedSearch SavedSearch { get; set; } = null!;
}
