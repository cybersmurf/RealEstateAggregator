using Microsoft.EntityFrameworkCore;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services.Auth;

/// <summary>
/// Naplní scoped <see cref="CurrentUser"/> podle Authorization: Bearer nebo X-Api-Key.
/// Zákaznické klíče mají denní kvótu – při překročení vrací 429 ještě před endpointem.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next, string masterApiKey, ILogger<CurrentUserMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, RealEstateDbContext db)
    {
        // 1) Bearer token
        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = context.RequestServices.GetRequiredService<AuthTokenService>();
            var userId = tokens.Validate(authHeader["Bearer ".Length..].Trim());
            if (userId is not null)
            {
                var user = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, context.RequestAborted);
                if (user is not null)
                {
                    currentUser.Apply(user, "bearer");
                    await next(context);
                    return;
                }
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { title = "Unauthorized", detail = "Neplatný nebo expirovaný token. Přihlaste se znovu." });
            return;
        }

        // 2) X-Api-Key – hlavní klíč (App/MCP/scraper) nebo zákaznický
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) && !string.IsNullOrEmpty(providedKey))
        {
            var key = providedKey.ToString();
            if (FixedTimeEquals(key, masterApiKey))
            {
                var admin = await db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == UserPlans.DefaultAdminId, context.RequestAborted);
                if (admin is not null)
                    currentUser.Apply(admin, "master-key");
                else
                {
                    currentUser.UserId = UserPlans.DefaultAdminId;
                    currentUser.IsAdmin = true;
                    currentUser.Plan = UserPlans.Profi;
                    currentUser.AuthMethod = "master-key";
                }
                await next(context);
                return;
            }

            if (key.StartsWith(ApiKeyHashing.Prefix, StringComparison.Ordinal))
            {
                var hash = ApiKeyHashing.Hash(key);
                var apiKey = await db.ApiKeys
                    .Include(k => k.User)
                    .FirstOrDefaultAsync(k => k.KeyHash == hash && k.IsActive, context.RequestAborted);

                if (apiKey is null || !apiKey.User.IsActive)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { title = "Unauthorized", detail = "Neplatný API klíč." });
                    return;
                }

                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (apiKey.QuotaDay != today)
                {
                    apiKey.QuotaDay = today;
                    apiKey.UsedToday = 0;
                }

                if (apiKey.UsedToday >= apiKey.DailyQuota)
                {
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.Response.Headers.RetryAfter = SecondsToMidnightUtc().ToString();
                    await context.Response.WriteAsJsonAsync(new
                    {
                        title = "Quota exceeded",
                        detail = $"Denní kvóta {apiKey.DailyQuota} požadavků vyčerpána. Obnoví se o půlnoci UTC.",
                    });
                    return;
                }

                apiKey.UsedToday++;
                apiKey.TotalRequests++;
                apiKey.LastUsedAt = DateTime.UtcNow;
                try
                {
                    await db.SaveChangesAsync(context.RequestAborted);
                }
                catch (DbUpdateException ex)
                {
                    // Počítadlo je best-effort – souběh dvou požadavků nesmí shodit volání
                    logger.LogDebug(ex, "API key counter update failed for {Prefix}", apiKey.KeyPrefix);
                }

                context.Response.Headers["X-RateLimit-Limit"] = apiKey.DailyQuota.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, apiKey.DailyQuota - apiKey.UsedToday).ToString();

                currentUser.Apply(apiKey.User, "api-key");
                await next(context);
                return;
            }
        }

        // 3) Anonym
        await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));

    private static int SecondsToMidnightUtc()
    {
        var now = DateTime.UtcNow;
        return (int)(now.Date.AddDays(1) - now).TotalSeconds;
    }
}
