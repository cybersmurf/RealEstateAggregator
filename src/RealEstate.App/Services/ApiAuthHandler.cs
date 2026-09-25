using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace RealEstate.App.Services;

/// <summary>
/// Doplňuje k voláním API identitu přihlášeného uživatele:
///   • <c>Authorization: Bearer</c> z claimu <see cref="TokenClaim"/> (token vydalo API při loginu),
///   • hlavní <c>X-Api-Key</c> jen pro <c>/api/scraping/*</c> a jen správci – dřív ho App posílala
///     na každý request, takže i anonymní návštěvník jednal vůči API jako vlastník.
/// Je scoped (per Blazor okruh): vlastní <see cref="AuthenticationStateProvider"/> daného okruhu,
/// proto se HttpClient skládá ručně v Program.cs místo přes IHttpClientFactory
/// (handlery z factory žijí v jiném DI scope a stav přihlášení by neviděly).
/// </summary>
public sealed class ApiAuthHandler(AuthenticationStateProvider authState, string scrapingApiKey) : DelegatingHandler
{
    public const string TokenClaim = "api_token";
    public const string AdminClaim = "is_admin";
    public const string PlanClaim = "plan";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ClaimsPrincipal? user = null;
        try
        {
            user = (await authState.GetAuthenticationStateAsync()).User;
        }
        catch
        {
            // Mimo okruh (např. při prerenderu bez HttpContextu) není stav k dispozici – pošleme anonymně
        }

        if (user?.Identity?.IsAuthenticated == true)
        {
            var token = user.FindFirstValue(TokenClaim);
            if (!string.IsNullOrEmpty(token) && request.Headers.Authorization is null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var isAdmin = string.Equals(user.FindFirstValue(AdminClaim), "true", StringComparison.OrdinalIgnoreCase);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (isAdmin && path.StartsWith("/api/scraping", StringComparison.OrdinalIgnoreCase)
                && !request.Headers.Contains("X-Api-Key"))
            {
                request.Headers.Add("X-Api-Key", scrapingApiKey);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
