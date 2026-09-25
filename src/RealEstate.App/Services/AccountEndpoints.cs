using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using RealEstate.Api.Contracts.Auth;

namespace RealEstate.App.Services;

/// <summary>
/// Přihlášení/registrace/odhlášení přes klasický form POST (mimo Blazor okruh),
/// protože cookie se dá nastavit jen v HTTP odpovědi – interaktivní komponenta
/// po navázání SignalR spojení už HttpContext nemá. API vydá bearer token,
/// ten se uloží do cookie jako claim a <see cref="ApiAuthHandler"/> ho přikládá k volání API.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Antiforgery vypnuté záměrně: formuláře renderuje interaktivní Blazor a token by
        // vyžadoval SSR stránku; login-CSRF chrání SameSite=Lax cookie.
        app.MapPost("/account/login", LoginAsync).DisableAntiforgery();
        app.MapPost("/account/register", RegisterAsync).DisableAntiforgery();
        // Odhlášení i přes GET – odkaz z menu; riziko „cizího odhlášení" je zanedbatelné
        app.MapMethods("/account/sign-out", ["GET", "POST"], LogoutAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> LoginAsync(HttpContext ctx, IHttpClientFactory factory, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        var api = factory.CreateClient("RealEstateApi");
        using var response = await api.PostAsJsonAsync("api/auth/login", new LoginRequestDto(email, password), ct);
        if (!response.IsSuccessStatusCode)
            return Results.Redirect($"/login?error={Uri.EscapeDataString(await ErrorDetailAsync(response, ct))}&returnUrl={Uri.EscapeDataString(returnUrl)}");

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(cancellationToken: ct);
        if (auth is null)
            return Results.Redirect("/login?error=Neplatn%C3%A1%20odpov%C4%9B%C4%8F%20API");

        await SignInAsync(ctx, auth);
        return Results.LocalRedirect(returnUrl);
    }

    private static async Task<IResult> RegisterAsync(HttpContext ctx, IHttpClientFactory factory, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var password2 = form["password2"].ToString();
        var displayName = form["displayName"].ToString();
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        if (password != password2)
            return Results.Redirect("/register?error=" + Uri.EscapeDataString("Hesla se neshodují."));

        var api = factory.CreateClient("RealEstateApi");
        using var response = await api.PostAsJsonAsync("api/auth/register",
            new RegisterRequestDto(email, password, string.IsNullOrWhiteSpace(displayName) ? null : displayName), ct);
        if (!response.IsSuccessStatusCode)
            return Results.Redirect($"/register?error={Uri.EscapeDataString(await ErrorDetailAsync(response, ct))}");

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>(cancellationToken: ct);
        if (auth is null)
            return Results.Redirect("/register?error=Neplatn%C3%A1%20odpov%C4%9B%C4%8F%20API");

        await SignInAsync(ctx, auth);
        return Results.LocalRedirect(returnUrl == "/" ? "/account?welcome=1" : returnUrl);
    }

    private static async Task LogoutAsync(HttpContext ctx)
    {
        // Cookie handler při SignOut zapíše hlavičky sám; návratový IResult s redirectem se
        // v praxi neprojevil (200 bez Location), proto přesměrování nastavíme přímo na Response.
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        ctx.Response.Redirect("/");
    }

    private static async Task SignInAsync(HttpContext ctx, AuthResponseDto auth)
    {
        var u = auth.User;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Email, u.Email),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName),
            new(ApiAuthHandler.PlanClaim, u.Plan),
            new(ApiAuthHandler.AdminClaim, u.IsAdmin ? "true" : "false"),
            new(ApiAuthHandler.TokenClaim, auth.Token),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = auth.ExpiresAt,
                AllowRefresh = false,
            });
    }

    private static string SafeReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith('/') && !value.StartsWith("//") ? value : "/";

    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsLite>(cancellationToken: ct);
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
                return problem.Detail;
        }
        catch
        {
            // Není JSON ProblemDetails – spadneme na obecnou hlášku
        }
        return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? "Příliš mnoho pokusů. Zkuste to za minutu."
            : "Přihlášení se nezdařilo.";
    }

    private sealed record ProblemDetailsLite(string? Title, string? Detail);
}
