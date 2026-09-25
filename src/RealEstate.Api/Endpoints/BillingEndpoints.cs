using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Billing;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Api.Services.Billing;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Endpoints;

public static class BillingEndpoints
{
    private static readonly string[] Intervals = ["month", "year"];

    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Billing");

        group.MapGet("/status", Status)
            .WithName("GetBillingStatus")
            .WithSummary("Tarif, platnost a zda má uživatel aktivní Stripe předplatné.")
            .RequireAuth();

        group.MapPost("/checkout", Checkout)
            .WithName("CreateCheckoutSession")
            .WithSummary("Založí Stripe Checkout Session; klient přesměruje na vrácenou URL.")
            .RequireAuth();

        group.MapPost("/portal", Portal)
            .WithName("CreateBillingPortalSession")
            .WithSummary("Stripe Billing Portal – správa karty a zrušení předplatného.")
            .RequireAuth();

        // Anonymní – ověřuje se podpisem Stripe-Signature, ne účtem
        group.MapPost("/webhook", Webhook)
            .WithName("StripeWebhook")
            .WithSummary("Stripe webhook (checkout.session.completed, customer.subscription.*).")
            .ExcludeFromDescription();

        var admin = group.MapGroup("/admin").RequireAdmin();
        admin.MapPost("/set-plan", SetPlan)
            .WithName("AdminSetPlan")
            .WithSummary("Ruční přidělení tarifu (testování, dárek).");
        admin.MapGet("/users", ListUsers)
            .WithName("AdminListUsers")
            .WithSummary("Přehled uživatelů (max 500).");

        return app;
    }

    private static async Task<IResult> Status(
        [FromServices] ICurrentUser current, [FromServices] RealEstateDbContext db,
        [FromServices] IStripeBillingService billing, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId!.Value, ct);
        if (user is null)
            return Results.NotFound();
        return Results.Ok(new BillingStatusDto(
            user.Plan, user.PlanValidUntil, user.StripeSubscriptionId is not null, billing.IsConfigured, null));
    }

    private static async Task<IResult> Checkout(
        [FromBody] CheckoutRequestDto request, [FromServices] ICurrentUser current,
        [FromServices] RealEstateDbContext db, [FromServices] IStripeBillingService billing, CancellationToken ct)
    {
        if (!billing.IsConfigured)
            return Results.Problem(title: "Billing not configured",
                detail: "Platby zatím nejsou aktivní.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var plan = request.Plan?.Trim().ToLowerInvariant();
        var interval = request.Interval?.Trim().ToLowerInvariant();
        if (plan is not (UserPlans.Hledac or UserPlans.Profi))
            return BadRequest("Neznámý tarif – povolené hodnoty: hledac, profi.");
        if (interval is null || !Intervals.Contains(interval))
            return BadRequest("Neznámý interval – povolené hodnoty: month, year.");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId!.Value, ct);
        if (user is null)
            return Results.NotFound();

        try
        {
            var url = await billing.CreateCheckoutSessionAsync(user, plan, interval, ct);
            return Results.Ok(new CheckoutSessionDto(url));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Checkout failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Portal(
        [FromServices] ICurrentUser current, [FromServices] RealEstateDbContext db,
        [FromServices] IStripeBillingService billing, CancellationToken ct)
    {
        if (!billing.IsConfigured)
            return Results.Problem(title: "Billing not configured",
                detail: "Platby zatím nejsou aktivní.", statusCode: StatusCodes.Status503ServiceUnavailable);

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId!.Value, ct);
        if (user is null)
            return Results.NotFound();

        try
        {
            var url = await billing.CreatePortalSessionAsync(user, ct);
            if (url is null)
                return BadRequest("K účtu není navázané žádné předplatné.");
            return Results.Ok(new BillingStatusDto(
                user.Plan, user.PlanValidUntil, user.StripeSubscriptionId is not null, true, url));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Portal failed", detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Webhook(HttpContext ctx, [FromServices] IStripeBillingService billing, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var payload = await reader.ReadToEndAsync(ct);
        var signature = ctx.Request.Headers["Stripe-Signature"].FirstOrDefault();

        var result = await billing.HandleWebhookAsync(payload, signature, ct);
        if (result.EventType == StripeBillingService.InvalidSignatureEventType)
            return BadRequest(result.Note ?? "Neplatný podpis.");
        return Results.Ok(result);
    }

    private static async Task<IResult> SetPlan(
        [FromBody] SetPlanRequestDto request, [FromServices] RealEstateDbContext db,
        [FromServices] ILogger<StripeBillingService> logger, [FromServices] ICurrentUser admin, CancellationToken ct)
    {
        var plan = request.Plan?.Trim().ToLowerInvariant();
        if (!UserPlans.IsKnown(plan))
            return BadRequest("Neznámý tarif – povolené hodnoty: free, hledac, profi.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null)
            return Results.NotFound();

        user.Plan = plan!;
        user.PlanValidUntil = plan == UserPlans.Free
            ? null
            : request.ValidUntil is { } until ? DateTime.SpecifyKind(until, DateTimeKind.Utc) : null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Admin {Admin} set plan {Plan} until {Until} for {Email}",
            admin.Email, user.Plan, user.PlanValidUntil, user.Email);
        return Results.Ok(MapUser(user));
    }

    private static async Task<IResult> ListUsers([FromServices] RealEstateDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .OrderByDescending(u => u.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
        return Results.Ok(users.Select(MapUser).ToList());
    }

    private static AdminUserDto MapUser(User u) =>
        new(u.Id, u.Email, u.Plan, u.PlanValidUntil, u.CreatedAt, u.LastLoginAt, u.IsAdmin);

    private static IResult BadRequest(string detail) =>
        Results.Problem(title: "Bad request", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
