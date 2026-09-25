namespace RealEstate.Api.Services.Notifications;

/// <summary>Odeslání e-mailu (HTML + textová alternativa). Implementace přes SMTP.</summary>
public interface IEmailSender
{
    /// <summary>False, když chybí Smtp:Host – odesílání se pak přeskočí místo výjimky.</summary>
    bool IsConfigured { get; }

    Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct);
}
