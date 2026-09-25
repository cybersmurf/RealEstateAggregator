using System.Net;
using System.Net.Mail;
using System.Net.Mime;

namespace RealEstate.Api.Services.Notifications;

/// <summary>
/// SMTP odesílání přes System.Net.Mail se STARTTLS (port 587). Konfigurace Smtp:Host/Port/User/Password/From.
/// Bez Smtp:Host je odesílání vypnuté – zaloguje se varování a nic se neposílá.
/// </summary>
public sealed class SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly string? _host = configuration["Smtp:Host"];
    private readonly int _port = configuration.GetValue<int?>("Smtp:Port") ?? 587;
    private readonly string? _user = configuration["Smtp:User"];
    private readonly string? _password = configuration["Smtp:Password"];
    private readonly string? _from = configuration["Smtp:From"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_host);

    public async Task SendAsync(string to, string subject, string htmlBody, string textBody, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("SMTP není nakonfigurováno (Smtp:Host) – e-mail pro {To} „{Subject}“ se neodeslal", to, subject);
            return;
        }

        var from = string.IsNullOrWhiteSpace(_from) ? _user ?? "noreply@localhost" : _from;

        using var message = new MailMessage
        {
            From = new MailAddress(from, "Realitní agregátor"),
            Subject = subject,
            Body = textBody,
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(to));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(htmlBody, null, MediaTypeNames.Text.Html));

        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = true, // STARTTLS na 587
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };
        if (!string.IsNullOrWhiteSpace(_user))
            client.Credentials = new NetworkCredential(_user, _password);

        await client.SendMailAsync(message, ct);
        logger.LogInformation("E-mail „{Subject}“ odeslán na {To}", subject, to);
    }
}
