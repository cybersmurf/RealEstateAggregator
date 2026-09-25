namespace RealEstate.Api.Contracts.Billing;

/// <param name="Plan">"hledac" | "profi"</param>
/// <param name="Interval">"month" | "year"</param>
public sealed record CheckoutRequestDto(string Plan, string Interval);

/// <param name="Url">Stripe Checkout URL – klient na ni přesměruje (forceLoad).</param>
public sealed record CheckoutSessionDto(string Url);

/// <param name="BillingConfigured">False = Stripe není nastaven, platby nejsou aktivní.</param>
/// <param name="PortalUrl">Vyplněno jen v odpovědi POST /api/billing/portal.</param>
public sealed record BillingStatusDto(
    string Plan,
    DateTime? PlanValidUntil,
    bool HasSubscription,
    bool BillingConfigured,
    string? PortalUrl);

/// <summary>Ruční přidělení tarifu adminem (testování, dárek). ValidUntil null = bez expirace.</summary>
public sealed record SetPlanRequestDto(Guid UserId, string Plan, DateTime? ValidUntil);

/// <summary>Výsledek zpracování Stripe webhooku – pro log a odpověď.</summary>
public sealed record WebhookResultDto(string EventType, bool Handled, string? Note);

/// <summary>Řádek přehledu uživatelů pro admina.</summary>
public sealed record AdminUserDto(
    Guid Id,
    string Email,
    string Plan,
    DateTime? PlanValidUntil,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    bool IsAdmin);
