namespace RealEstate.Domain.Entities;

/// <summary>
/// Účet uživatele aplikace. Tarify: "free" | "hledac" | "profi" (viz <see cref="UserPlans"/>).
/// Výchozí admin účet má pevné Id <see cref="UserPlans.DefaultAdminId"/> – na něj jsou navázané
/// historické záznamy user_listing_state, které vznikly před zavedením účtů.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = null!;
    /// <summary>PBKDF2 hash; null = účet bez hesla (nelze se přihlásit, dokud se nenastaví).</summary>
    public string? PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public string Plan { get; set; } = UserPlans.Free;
    /// <summary>Do kdy platí placený tarif. Null u free nebo u ručně přiděleného tarifu bez expirace.</summary>
    public DateTime? PlanValidUntil { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    /// <summary>Telegram chat_id pro doručení upozornění (uživatel získá napsáním botu).</summary>
    public string? TelegramChatId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public ICollection<SavedSearch> SavedSearches { get; set; } = new List<SavedSearch>();
    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
}

public static class UserPlans
{
    public const string Free = "free";
    public const string Hledac = "hledac";
    public const string Profi = "profi";

    /// <summary>Historický „výchozí uživatel" – od zavedení účtů je to admin účet vlastníka.</summary>
    public static readonly Guid DefaultAdminId = new("00000000-0000-0000-0000-000000000001");

    public static int Rank(string? plan) => plan switch
    {
        Profi => 2,
        Hledac => 1,
        _ => 0,
    };

    public static bool IsKnown(string? plan) => plan is Free or Hledac or Profi;
}
