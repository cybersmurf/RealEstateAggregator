using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Billing;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Billing;

public sealed class StripeBillingService(
    RealEstateDbContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<StripeBillingService> logger) : IStripeBillingService
{
    public const string InvalidSignatureEventType = "invalid_signature";

    /// <summary>Tolerance stáří podpisu – ochrana proti replay útoku.</summary>
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Provizorní platnost po checkoutu, než dorazí subscription event s přesným koncem období.</summary>
    private static readonly TimeSpan ProvisionalValidity = TimeSpan.FromDays(35);

    /// <summary>Rezerva po konci období – obnova se účtuje s odstupem, nechceme uživatele vyhodit hned.</summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromDays(3);

    private string? SecretKey => config["Stripe:SecretKey"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);

    private string AppPublicUrl =>
        (Environment.GetEnvironmentVariable("APP_PUBLIC_URL") ?? config["APP_PUBLIC_URL"] ?? "http://localhost:5002").TrimEnd('/');

    // ── Checkout & portál ────────────────────────────────────────────────

    public async Task<string> CreateCheckoutSessionAsync(User user, string plan, string interval, CancellationToken ct)
    {
        var priceId = PriceIdFor(plan, interval);
        if (string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException(
                $"Pro tarif „{plan}“ a interval „{interval}“ není nastavené Stripe price id ({PriceConfigKey(plan, interval)}).");

        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("line_items[0][price]", priceId),
            new("line_items[0][quantity]", "1"),
            new("success_url", $"{AppPublicUrl}/account?checkout=success"),
            new("cancel_url", $"{AppPublicUrl}/pricing?checkout=cancel"),
            new("client_reference_id", user.Id.ToString()),
            new("metadata[user_id]", user.Id.ToString()),
            new("metadata[plan]", plan),
            // Metadata i na subscription – events customer.subscription.* pak nesou plan/user_id
            new("subscription_data[metadata][user_id]", user.Id.ToString()),
            new("subscription_data[metadata][plan]", plan),
            new("allow_promotion_codes", "true"),
        };

        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
            form.Add(new("customer_email", user.Email));
        else
            form.Add(new("customer", user.StripeCustomerId));

        using var doc = await PostFormAsync("/v1/checkout/sessions", form, ct);
        var url = GetString(doc.RootElement, "url");
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("Stripe nevrátil URL Checkout Session.");

        logger.LogInformation("Stripe checkout session for {UserId} plan={Plan} interval={Interval}", user.Id, plan, interval);
        return url;
    }

    public async Task<string?> CreatePortalSessionAsync(User user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
            return null;

        var form = new List<KeyValuePair<string, string>>
        {
            new("customer", user.StripeCustomerId),
            new("return_url", $"{AppPublicUrl}/account"),
        };

        using var doc = await PostFormAsync("/v1/billing_portal/sessions", form, ct);
        return GetString(doc.RootElement, "url");
    }

    private async Task<JsonDocument> PostFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Stripe není nakonfigurován (Stripe:SecretKey).");

        var client = httpClientFactory.CreateClient("Stripe");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.stripe.com" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SecretKey);
        request.Content = new FormUrlEncodedContent(form);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadStripeError(body);
            logger.LogWarning("Stripe {Path} failed {Status}: {Message}", path, (int)response.StatusCode, message);
            throw new InvalidOperationException($"Stripe odmítl požadavek: {message}");
        }
        return JsonDocument.Parse(body);
    }

    private static string TryReadStripeError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var message = GetString(err, "message");
                if (message is not null)
                    return message;
            }
        }
        catch (JsonException)
        {
            // Ne-JSON odpověď – vrátíme tělo tak, jak je
        }
        return body.Length > 300 ? body[..300] : body;
    }

    // ── Mapování tarif ↔ price id ────────────────────────────────────────

    /// <summary>Klíč konfigurace, např. Stripe:PriceHledacMonthly / Stripe:PriceProfiYearly.</summary>
    public static string PriceConfigKey(string plan, string interval) =>
        $"Stripe:Price{Capitalize(plan)}{(interval == "year" ? "Yearly" : "Monthly")}";

    private string? PriceIdFor(string plan, string interval) => config[PriceConfigKey(plan, interval)];

    private IReadOnlyDictionary<string, string> PriceToPlanMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in new[] { UserPlans.Hledac, UserPlans.Profi })
        foreach (var interval in new[] { "month", "year" })
        {
            var id = PriceIdFor(plan, interval);
            if (!string.IsNullOrWhiteSpace(id))
                map[id] = plan;
        }
        return map;
    }

    /// <summary>Tarif podle price id ze subscription; null, když id neznáme.</summary>
    public static string? ParsePlanFromPrice(string? priceId, IReadOnlyDictionary<string, string> priceToPlan) =>
        priceId is not null && priceToPlan.TryGetValue(priceId, out var plan) ? plan : null;

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ── Webhook ──────────────────────────────────────────────────────────

    public async Task<WebhookResultDto> HandleWebhookAsync(string payload, string? signatureHeader, CancellationToken ct)
    {
        var secret = config["Stripe:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            return new WebhookResultDto(InvalidSignatureEventType, false, "Stripe:WebhookSecret není nastaven.");

        if (!VerifySignature(payload, signatureHeader ?? "", secret, DateTimeOffset.UtcNow, out var error))
        {
            logger.LogWarning("Stripe webhook rejected: {Error}", error);
            return new WebhookResultDto(InvalidSignatureEventType, false, error);
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var eventType = GetString(root, "type") ?? "";
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj))
            return new WebhookResultDto(eventType, false, "Chybí data.object.");

        var result = eventType switch
        {
            "checkout.session.completed" => await HandleCheckoutCompletedAsync(obj, ct),
            "customer.subscription.created" or "customer.subscription.updated" => await HandleSubscriptionChangedAsync(obj, ct),
            "customer.subscription.deleted" => await HandleSubscriptionDeletedAsync(obj, ct),
            _ => new WebhookResultDto(eventType, false, "Událost ignorována."),
        };

        result = result with { EventType = eventType };
        logger.LogInformation("Stripe webhook {Type} handled={Handled}: {Note}", eventType, result.Handled, result.Note);
        return result;
    }

    /// <summary>
    /// Ověření hlavičky Stripe-Signature (`t=…,v1=…`). Podepsaný řetězec je `{t}.{payload}`,
    /// HMAC-SHA256 s webhook secretem, porovnání v konstantním čase, tolerance 5 minut.
    /// </summary>
    public static bool VerifySignature(string payload, string header, string secret, DateTimeOffset now, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            error = "Chybí hlavička Stripe-Signature.";
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
                timestamp = ts;
            else if (key == "v1")
                signatures.Add(value);
        }

        if (timestamp is null || signatures.Count == 0)
        {
            error = "Hlavička Stripe-Signature nemá očekávaný formát (t=…,v1=…).";
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age > SignatureTolerance || age < -SignatureTolerance)
        {
            error = $"Podpis je mimo toleranci ({age.TotalSeconds:F0} s).";
            return false;
        }

        var expectedBytes = Encoding.ASCII.GetBytes(ComputeSignature(payload, timestamp.Value, secret));
        foreach (var sig in signatures)
        {
            var sigBytes = Encoding.ASCII.GetBytes(sig.ToLowerInvariant());
            if (sigBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(sigBytes, expectedBytes))
                return true;
        }

        error = "Podpis nesouhlasí.";
        return false;
    }

    /// <summary>Hex HMAC-SHA256 nad `{timestamp}.{payload}` – stejný výpočet používá Stripe i testy.</summary>
    public static string ComputeSignature(string payload, long timestamp, string secret)
    {
        var signed = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<WebhookResultDto> HandleCheckoutCompletedAsync(JsonElement session, CancellationToken ct)
    {
        var userRef = GetString(session, "client_reference_id") ?? GetMetadata(session, "user_id");
        if (!Guid.TryParse(userRef, out var userId))
            return new WebhookResultDto("", false, "Session nemá client_reference_id / metadata.user_id.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new WebhookResultDto("", false, $"Uživatel {userId} nenalezen.");

        var customer = GetString(session, "customer");
        var subscription = GetString(session, "subscription");
        var plan = GetMetadata(session, "plan");

        if (customer is not null)
            user.StripeCustomerId = customer;
        if (subscription is not null)
            user.StripeSubscriptionId = subscription;
        if (IsPaidPlan(plan))
        {
            user.Plan = plan!;
            // Provizorně – přesný konec období nastaví customer.subscription.created/updated
            user.PlanValidUntil = DateTime.UtcNow + ProvisionalValidity;
        }

        await db.SaveChangesAsync(ct);
        return new WebhookResultDto("", true, $"Uživatel {user.Email}: tarif {user.Plan} (provizorně).");
    }

    private async Task<WebhookResultDto> HandleSubscriptionChangedAsync(JsonElement subscription, CancellationToken ct)
    {
        var info = ParseSubscription(subscription);
        var user = await FindBySubscriptionAsync(info, ct);
        if (user is null)
            return new WebhookResultDto("", false, $"Uživatel pro subscription {info.Id} / customer {info.CustomerId} nenalezen.");

        if (info.Id is not null)
            user.StripeSubscriptionId = info.Id;
        if (info.CustomerId is not null)
            user.StripeCustomerId = info.CustomerId;

        switch (info.Status)
        {
            case "active" or "trialing":
            {
                var plan = IsPaidPlan(info.MetadataPlan)
                    ? info.MetadataPlan!
                    : ParsePlanFromPrice(info.PriceId, PriceToPlanMap()) ?? user.Plan;
                if (!IsPaidPlan(plan))
                    return new WebhookResultDto("", false, $"Nelze určit tarif (price {info.PriceId}).");

                user.Plan = plan;
                if (info.CurrentPeriodEnd is { } end)
                    user.PlanValidUntil = end.UtcDateTime + GracePeriod;
                break;
            }
            case "past_due":
                // Tarif necháme, ale platnost neprodlužujeme – doběhne s koncem období
                break;
            case "canceled" or "unpaid" or "incomplete_expired":
                user.Plan = UserPlans.Free;
                user.PlanValidUntil = null;
                break;
            default:
                await db.SaveChangesAsync(ct);
                return new WebhookResultDto("", false, $"Stav subscription „{info.Status}“ ignorován.");
        }

        await db.SaveChangesAsync(ct);
        return new WebhookResultDto("", true,
            $"Uživatel {user.Email}: {info.Status} → tarif {user.Plan}, platí do {user.PlanValidUntil:yyyy-MM-dd}.");
    }

    private async Task<WebhookResultDto> HandleSubscriptionDeletedAsync(JsonElement subscription, CancellationToken ct)
    {
        var info = ParseSubscription(subscription);
        var user = await FindBySubscriptionAsync(info, ct);
        if (user is null)
            return new WebhookResultDto("", false, $"Uživatel pro subscription {info.Id} nenalezen.");

        user.Plan = UserPlans.Free;
        user.PlanValidUntil = null;
        user.StripeSubscriptionId = null;
        await db.SaveChangesAsync(ct);
        return new WebhookResultDto("", true, $"Uživatel {user.Email}: předplatné zrušeno → free.");
    }

    private Task<User?> FindBySubscriptionAsync(SubscriptionInfo info, CancellationToken ct)
    {
        if (Guid.TryParse(info.MetadataUserId, out var userId))
            return db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        return db.Users.FirstOrDefaultAsync(u =>
            (info.Id != null && u.StripeSubscriptionId == info.Id)
            || (info.CustomerId != null && u.StripeCustomerId == info.CustomerId), ct);
    }

    private static bool IsPaidPlan(string? plan) => plan is UserPlans.Hledac or UserPlans.Profi;

    /// <summary>Podmnožina objektu subscription, kterou potřebujeme.</summary>
    public sealed record SubscriptionInfo(
        string? Id,
        string? CustomerId,
        string? Status,
        string? PriceId,
        string? MetadataPlan,
        string? MetadataUserId,
        DateTimeOffset? CurrentPeriodEnd);

    /// <summary>Čistý parser objektu subscription (data.object) – bez DB, testovatelný.</summary>
    public static SubscriptionInfo ParseSubscription(JsonElement sub)
    {
        string? priceId = null;
        var periodEnd = GetLong(sub, "current_period_end");

        if (sub.TryGetProperty("items", out var items)
            && items.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (priceId is null && item.TryGetProperty("price", out var price))
                    priceId = price.ValueKind == JsonValueKind.String ? price.GetString() : GetString(price, "id");
                // Novější API verze (2025+) mají konec období na položce, ne na subscription
                periodEnd ??= GetLong(item, "current_period_end");
            }
        }

        return new SubscriptionInfo(
            GetString(sub, "id"),
            GetString(sub, "customer"),
            GetString(sub, "status"),
            priceId,
            GetMetadata(sub, "plan"),
            GetMetadata(sub, "user_id"),
            periodEnd is { } end ? DateTimeOffset.FromUnixTimeSeconds(end) : null);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            // Expandované objekty (customer: {id: ...})
            JsonValueKind.Object => GetString(p, "id"),
            _ => null,
        };
    }

    private static long? GetLong(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt64(out var v)
            ? v
            : null;

    private static string? GetMetadata(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("metadata", out var meta) ? GetString(meta, key) : null;
}
