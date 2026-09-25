using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services.SavedSearches;

/// <summary>
/// Kontrola tarifu nad entitou <see cref="User"/> – stejná logika jako <c>CurrentUser.HasPlan</c>,
/// ale použitelná v background jobu, kde žádný request ani ICurrentUser není.
/// </summary>
public static class PlanAccess
{
    /// <summary>Efektivní tarif: expirovaný placený tarif se počítá jako free.</summary>
    public static string EffectivePlan(User user, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return user.PlanValidUntil is { } until && until < now ? UserPlans.Free : user.Plan;
    }

    /// <summary>Má uživatel aspoň daný tarif? Admin vždy ano.</summary>
    public static bool HasPlan(User user, string plan, DateTime? nowUtc = null) =>
        user.IsAdmin || UserPlans.Rank(EffectivePlan(user, nowUtc)) >= UserPlans.Rank(plan);

    /// <summary>Kolik uložených hledání si uživatel může založit (null = bez limitu).</summary>
    public static int? MaxSavedSearches(User user, DateTime? nowUtc = null)
    {
        if (user.IsAdmin) return null;
        return EffectivePlan(user, nowUtc) switch
        {
            UserPlans.Profi => 50,
            UserPlans.Hledac => 10,
            _ => 0,
        };
    }
}
