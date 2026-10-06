using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services.Auth;

/// <summary>
/// Identita volajícího pro aktuální request. Plní <see cref="CurrentUserMiddleware"/>:
///   • Bearer token → konkrétní uživatel,
///   • hlavní X-Api-Key (API_KEY) → výchozí admin (Blazor App pro scraping, MCP server, scraper),
///   • zákaznický X-Api-Key → vlastník klíče (s denní kvótou),
///   • nic → anonym (UserId = null, žádné osobní stavy).
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    string Plan { get; }
    bool IsAdmin { get; }
    bool IsAuthenticated { get; }
    /// <summary>Jak byl uživatel identifikován: "bearer" | "master-key" | "api-key" | "anonymous".</summary>
    string AuthMethod { get; }

    /// <summary>
    /// Id pro dotazy na user_listing_state. Člen společného prostoru pracuje se stavy vlastníka
    /// prostoru; anonym dostane Guid.Empty – žádné řádky, takže osobní poznámky nejsou veřejně vidět.
    /// </summary>
    Guid EffectiveUserId { get; }

    /// <summary>Role ve společném prostoru jiného účtu ("reader" / "writer"), null = vlastní prostor.</summary>
    string? WorkspaceRole { get; }

    /// <summary>Smí měnit stavy a poznámky: ve vlastním prostoru vždy, v cizím jen s rolí writer.</summary>
    bool CanWriteWorkspace { get; }

    /// <summary>
    /// Smí vidět fotky z prohlídek, analýzy a porovnání: správce, nebo člen prostoru správce.
    /// Tato data nejsou vedená po uživatelích (patří vlastníkovi aplikace), proto nestačí být členem
    /// prostoru běžného účtu.
    /// </summary>
    bool CanSeeInspectionRecords { get; }

    /// <summary>Má uživatel aspoň daný tarif (admin vždy ano)? Expirovaný placený tarif = free.</summary>
    bool HasPlan(string plan);
}

public sealed class CurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }
    public string? Email { get; set; }
    public string Plan { get; set; } = UserPlans.Free;
    public DateTime? PlanValidUntil { get; set; }
    public bool IsAdmin { get; set; }
    public string AuthMethod { get; set; } = "anonymous";

    public Guid? WorkspaceOwnerId { get; set; }
    public string? WorkspaceRole { get; set; }
    public bool WorkspaceOwnerIsAdmin { get; set; }

    public bool IsAuthenticated => UserId is not null;

    public Guid EffectiveUserId => WorkspaceOwnerId ?? UserId ?? Guid.Empty;

    public bool CanWriteWorkspace =>
        IsAuthenticated && (WorkspaceOwnerId is null || WorkspaceRole == WorkspaceRoles.Writer);

    public bool CanSeeInspectionRecords =>
        IsAdmin || (WorkspaceOwnerId is not null && WorkspaceOwnerIsAdmin);

    /// <summary>Zapíše členství ve společném prostoru (null = uživatel v žádném cizím prostoru není).</summary>
    public void ApplyWorkspace(WorkspaceAccess? access)
    {
        WorkspaceOwnerId = access?.OwnerId;
        WorkspaceRole = access?.Role;
        WorkspaceOwnerIsAdmin = access?.OwnerIsAdmin ?? false;
    }

    public string EffectivePlan =>
        PlanValidUntil is { } until && until < DateTime.UtcNow ? UserPlans.Free : Plan;

    public bool HasPlan(string plan) =>
        IsAdmin || UserPlans.Rank(EffectivePlan) >= UserPlans.Rank(plan);

    public void Apply(User user, string method)
    {
        UserId = user.Id;
        Email = user.Email;
        Plan = user.Plan;
        PlanValidUntil = user.PlanValidUntil;
        IsAdmin = user.IsAdmin;
        AuthMethod = method;
    }
}
