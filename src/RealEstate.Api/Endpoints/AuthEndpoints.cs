using Microsoft.AspNetCore.Mvc;
using RealEstate.Api.Contracts.Auth;
using RealEstate.Api.Helpers;
using RealEstate.Api.Services.Auth;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", Register)
            .WithName("AuthRegister")
            .WithSummary("Založí účet (tarif Zdarma) a vrátí bearer token.")
            .RequireRateLimiting("auth");

        group.MapPost("/login", Login)
            .WithName("AuthLogin")
            .WithSummary("Přihlášení e-mailem a heslem; vrací bearer token.")
            .RequireRateLimiting("auth");

        group.MapGet("/me", Me)
            .WithName("AuthMe")
            .WithSummary("Profil přihlášeného uživatele (tarif, admin, Telegram).")
            .RequireAuth();

        group.MapPatch("/me", UpdateMe)
            .WithName("AuthUpdateMe")
            .RequireAuth();

        group.MapPost("/change-password", ChangePassword)
            .WithName("AuthChangePassword")
            .RequireAuth();

        group.MapGet("/plans", Plans)
            .WithName("AuthPlans")
            .WithSummary("Ceník tarifů pro stránku /pricing.");

        // ── API klíče zákazníků (tarif Profi) ────────────────────────────────
        var keys = app.MapGroup("/api/keys").WithTags("ApiKeys").RequirePlan(UserPlans.Profi);
        keys.MapGet("", ListKeys).WithName("ListApiKeys");
        keys.MapPost("", CreateKey).WithName("CreateApiKey")
            .WithSummary("Vytvoří API klíč – plný klíč je v odpovědi jen jednou.");
        keys.MapDelete("/{id:guid}", RevokeKey).WithName("RevokeApiKey");

        return app;
    }

    private static async Task<IResult> Register(
        [FromBody] RegisterRequestDto request, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var (result, error) = await auth.RegisterAsync(request, ct);
        return error is null
            ? Results.Ok(result)
            : Results.Problem(title: "Registration failed", detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> Login(
        [FromBody] LoginRequestDto request, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var (result, error) = await auth.LoginAsync(request, ct);
        return error is null
            ? Results.Ok(result)
            : Results.Problem(title: "Login failed", detail: error, statusCode: StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> Me([FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var profile = await auth.GetProfileAsync(user.UserId!.Value, ct);
        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    private static async Task<IResult> UpdateMe(
        [FromBody] UpdateProfileRequestDto request, [FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var profile = await auth.UpdateProfileAsync(user.UserId!.Value, request, ct);
        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    private static async Task<IResult> ChangePassword(
        [FromBody] ChangePasswordRequestDto request, [FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var error = await auth.ChangePasswordAsync(user.UserId!.Value, request, ct);
        return error is null
            ? Results.NoContent()
            : Results.Problem(title: "Password change failed", detail: error, statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult Plans([FromServices] IConfiguration config)
    {
        decimal? Price(string key) => config.GetValue<decimal?>(key);
        var purchasable = !string.IsNullOrWhiteSpace(config["Stripe:SecretKey"]);

        var plans = new List<PlanInfoDto>
        {
            new(UserPlans.Free, "Zdarma", 0, 0,
            [
                "Vyhledávání a filtry napříč 14 zdroji",
                "Sloučená karta duplicit – jeden dům, všechny zdroje a ceny",
                "AI shrnutí inzerátu, historie ceny",
                "Mapa a katastr",
            ], false),
            new(UserPlans.Hledac, "Hledač",
                Price("Billing:HledacMonthlyCzk") ?? 199, Price("Billing:HledacYearlyCzk") ?? 1_990,
            [
                "Vše ze Zdarma",
                "Uložená hledání s upozorněním e-mailem a Telegramem",
                "Upozornění na zlevnění sledovaných inzerátů",
                "Cena za m² proti mediánu obce a dispozice",
            ], purchasable),
            new(UserPlans.Profi, "Profi",
                Price("Billing:ProfiMonthlyCzk") ?? 990, Price("Billing:ProfiYearlyCzk") ?? 9_900,
            [
                "Vše z Hledače",
                "Hrubý výnos z pronájmu podle obce a dispozice",
                "Doba na trhu, stažené inzeráty a tržní report",
                "Parametry dražeb (termín, vyvolávací cena, jistota)",
                "API klíče s denní kvótou",
            ], purchasable),
        };
        return Results.Ok(plans);
    }

    private static async Task<IResult> ListKeys([FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
        => Results.Ok(await auth.ListApiKeysAsync(user.UserId!.Value, ct));

    private static async Task<IResult> CreateKey(
        [FromBody] CreateApiKeyRequestDto request, [FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
    {
        var created = await auth.CreateApiKeyAsync(user.UserId!.Value, request, user.IsAdmin, ct);
        return created is null
            ? Results.Problem(title: "Limit reached", detail: "Maximálně 5 aktivních klíčů na účet.", statusCode: StatusCodes.Status400BadRequest)
            : Results.Ok(created);
    }

    private static async Task<IResult> RevokeKey(Guid id, [FromServices] ICurrentUser user, [FromServices] IAuthService auth, CancellationToken ct)
        => await auth.RevokeApiKeyAsync(user.UserId!.Value, id, ct) ? Results.NoContent() : Results.NotFound();
}
