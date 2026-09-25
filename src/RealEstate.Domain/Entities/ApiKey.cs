namespace RealEstate.Domain.Entities;

/// <summary>
/// API klíč zákazníka (tarif Profi). Ukládá se jen SHA-256 hash; prefix slouží k identifikaci v UI.
/// Kvóta se počítá per den (UTC) – UsedToday se nuluje, když se QuotaDay liší od dneška.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Name { get; set; } = null!;
    public string KeyHash { get; set; } = null!;
    /// <summary>Prvních 8 znaků klíče pro zobrazení („rea_ab12…").</summary>
    public string KeyPrefix { get; set; } = null!;
    public int DailyQuota { get; set; } = 1_000;
    public int UsedToday { get; set; }
    public DateOnly? QuotaDay { get; set; }
    public long TotalRequests { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
