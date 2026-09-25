using RealEstate.Api.Contracts.Billing;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services.Billing;

/// <summary>
/// Předplatné přes Stripe Checkout + Billing Portal. Bez SDK – volá REST API přímo
/// (form-encoded, Bearer secret key). Stav tarifu drží webhook, ne polling.
/// </summary>
public interface IStripeBillingService
{
    /// <summary>True, když je nastaven Stripe:SecretKey.</summary>
    bool IsConfigured { get; }

    /// <summary>Založí Checkout Session a vrátí URL, na kterou klient přesměruje.</summary>
    /// <exception cref="InvalidOperationException">Chybí price id pro danou kombinaci tarif/interval, nebo Stripe odmítl požadavek.</exception>
    Task<string> CreateCheckoutSessionAsync(User user, string plan, string interval, CancellationToken ct);

    /// <summary>URL Billing Portalu (změna karty, zrušení). Null, když uživatel nemá Stripe customer.</summary>
    Task<string?> CreatePortalSessionAsync(User user, CancellationToken ct);

    /// <summary>Ověří podpis a zpracuje událost. Neplatný podpis → EventType <see cref="StripeBillingService.InvalidSignatureEventType"/>.</summary>
    Task<WebhookResultDto> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct);
}
