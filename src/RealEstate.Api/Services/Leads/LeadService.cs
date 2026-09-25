using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Leads;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Leads;

public sealed class LeadService(
    RealEstateDbContext db,
    IConfiguration config,
    ILogger<LeadService> logger) : ILeadService
{
    private const int MaxNameLength = 200;
    private const int MaxMessageLength = 2000;

    public static readonly string[] KnownKinds = ["mortgage", "contact"];

    public async Task<(LeadDto? Lead, string? Error)> CreateAsync(LeadCreateDto request, Guid? userId, CancellationToken ct)
    {
        var error = Validate(request);
        if (error is not null)
            return (null, error);

        var lead = new Lead
        {
            ListingId = request.ListingId,
            UserId = userId,
            Kind = request.Kind.Trim().ToLowerInvariant(),
            Name = request.Name.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            Phone = Clean(request.Phone),
            Message = Clean(request.Message),
            PropertyPrice = request.PropertyPrice,
            LoanAmount = request.LoanAmount,
            LoanYears = request.LoanYears,
            Source = Clean(request.Source),
            Consent = request.Consent,
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);

        var listingTitle = lead.ListingId is { } listingId
            ? await db.Listings.AsNoTracking().Where(l => l.Id == listingId).Select(l => l.Title).FirstOrDefaultAsync(ct)
            : null;

        // Přeposlání partnerovi nesmí shodit request – lead už je uložený
        try
        {
            if (await ForwardAsync(lead, listingTitle, ct))
            {
                lead.ForwardedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Lead {LeadId} saved but forwarding failed", lead.Id);
        }

        logger.LogInformation("Lead {LeadId} kind={Kind} listing={ListingId} forwarded={Forwarded}",
            lead.Id, lead.Kind, lead.ListingId, lead.ForwardedAt is not null);
        return (Map(lead, listingTitle), null);
    }

    public async Task<List<LeadDto>> ListAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 500);
        var rows = await db.Leads.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Take(take)
            .Select(l => new
            {
                Lead = l,
                Title = db.Listings.Where(x => x.Id == l.ListingId).Select(x => x.Title).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(r => Map(r.Lead, r.Title)).ToList();
    }

    /// <summary>Validace vstupu; null = v pořádku, jinak česká hláška pro uživatele.</summary>
    public static string? Validate(LeadCreateDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return "Zadejte jméno.";
        if (request.Name.Trim().Length > MaxNameLength)
            return $"Jméno může mít nejvýše {MaxNameLength} znaků.";
        if (string.IsNullOrWhiteSpace(request.Email) || !MailAddress.TryCreate(request.Email.Trim(), out _))
            return "Zadejte platný e-mail.";
        if (!request.Consent)
            return "Bez souhlasu se zpracováním údajů nemůžeme poptávku předat.";
        if (string.IsNullOrWhiteSpace(request.Kind) || !KnownKinds.Contains(request.Kind.Trim().ToLowerInvariant()))
            return "Neznámý typ poptávky.";
        if (request.Message is { Length: > MaxMessageLength })
            return $"Poznámka může mít nejvýše {MaxMessageLength} znaků.";
        if (request.LoanYears is { } years && (years < 1 || years > 40))
            return "Splatnost musí být 1–40 let.";
        if (request.LoanAmount is < 0 || request.PropertyPrice is < 0)
            return "Částky nemohou být záporné.";
        return null;
    }

    // Jednoduché přeposlání přes SmtpClient – dokud není k dispozici sdílený IEmailSender.
    // Podmínka: Smtp:Host a Leads:NotifyEmail. Bez nich jen zalogujeme.
    private async Task<bool> ForwardAsync(Lead lead, string? listingTitle, CancellationToken ct)
    {
        var host = config["Smtp:Host"];
        var to = config["Leads:NotifyEmail"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Lead {LeadId} not forwarded – Smtp:Host or Leads:NotifyEmail not configured", lead.Id);
            return false;
        }

        var from = config["Smtp:From"];
        if (string.IsNullOrWhiteSpace(from))
            from = config["Smtp:User"];
        if (string.IsNullOrWhiteSpace(from))
            from = "noreply@realestate.local";

        using var message = new MailMessage(from, to)
        {
            Subject = $"[RealEstate] Nová poptávka – {KindLabel(lead.Kind)} – {lead.Name}",
            Body = BuildBody(lead, listingTitle),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
        };
        message.ReplyToList.Add(new MailAddress(lead.Email, lead.Name));

        using var client = new SmtpClient(host, config.GetValue<int?>("Smtp:Port") ?? 587)
        {
            EnableSsl = config.GetValue<bool?>("Smtp:EnableSsl") ?? true,
            Timeout = 15_000,
        };
        var user = config["Smtp:User"];
        if (!string.IsNullOrWhiteSpace(user))
            client.Credentials = new NetworkCredential(user, config["Smtp:Password"]);

        // SmtpClient.SendMailAsync(message, ct) respektuje zrušení requestu
        await client.SendMailAsync(message, ct);
        return true;
    }

    private static string BuildBody(Lead lead, string? listingTitle)
    {
        var cz = CultureInfo.GetCultureInfo("cs-CZ");
        var sb = new StringBuilder();
        sb.AppendLine($"Typ: {KindLabel(lead.Kind)}");
        sb.AppendLine($"Jméno: {lead.Name}");
        sb.AppendLine($"E-mail: {lead.Email}");
        if (lead.Phone is not null)
            sb.AppendLine($"Telefon: {lead.Phone}");
        if (lead.ListingId is not null)
            sb.AppendLine($"Inzerát: {listingTitle ?? "(bez názvu)"} ({lead.ListingId})");
        if (lead.PropertyPrice is { } price)
            sb.AppendLine($"Cena nemovitosti: {price.ToString("N0", cz)} Kč");
        if (lead.LoanAmount is { } loan)
            sb.AppendLine($"Výše úvěru: {loan.ToString("N0", cz)} Kč");
        if (lead.LoanYears is { } years)
            sb.AppendLine($"Splatnost: {years} let");
        if (lead.Message is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Poznámka:");
            sb.AppendLine(lead.Message);
        }
        sb.AppendLine();
        sb.AppendLine($"Zdroj: {lead.Source ?? "-"} · Souhlas: {(lead.Consent ? "ano" : "ne")} · {lead.CreatedAt:yyyy-MM-dd HH:mm} UTC");
        return sb.ToString();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "mortgage" => "Hypotéka",
        "contact" => "Kontakt",
        _ => kind,
    };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static LeadDto Map(Lead l, string? listingTitle) => new(
        l.Id, l.CreatedAt, l.Kind, l.Name, l.Email, l.Phone, l.ListingId, listingTitle,
        l.PropertyPrice, l.LoanAmount, l.LoanYears, l.ForwardedAt is not null);
}
