using System.Net.Http.Headers;
using System.Text.Json;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// Jednorázové OAuth nastavení pro OneDrive (Microsoft Graph).
///
/// Postup:
/// 0. Zaregistruj aplikaci v Azure: https://portal.azure.com → App registrations → New
///    - Platform: Web, Redirect URI: http://localhost:5001/api/auth/onedrive/callback
///    - API permissions: Microsoft Graph → Files.ReadWrite, offline_access (delegated)
///    - Nastav v appsettings: OneDriveExport:OAuthClientId + OAuthClientSecret
///
/// 1. GET  /api/auth/onedrive/setup  → vrátí URL kam přejít v prohlížeči
/// 2. Schválit přístup → přesměruje na /api/auth/onedrive/callback?code=…
/// 3. Token se uloží do secrets/onedrive-token.json
/// 4. Export na OneDrive nyní funguje
/// </summary>
public static class OneDriveAuthEndpoints
{
    private const string AuthBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string CallbackPath = "/api/auth/onedrive/callback";
    private const string Scopes = "Files.ReadWrite offline_access";

    public static IEndpointRouteBuilder MapOneDriveAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/onedrive/setup", SetupAuth)
            .WithName("OneDriveAuthSetup")
            .WithSummary("Vrátí URL pro jednorázové OAuth povolení OneDrive přístupu");

        app.MapGet("/api/auth/onedrive/callback", HandleCallback)
            .WithName("OneDriveAuthCallback")
            .WithSummary("Zpracuje OAuth callback a uloží token");

        return app;
    }

    private static IResult SetupAuth(IConfiguration configuration, HttpContext ctx)
    {
        var (clientId, _, tokenPath) = GetConfig(configuration);

        if (System.IO.File.Exists(tokenPath))
        {
            return Results.Ok(new
            {
                status = "already_configured",
                message = "OneDrive token již existuje. Export by měl fungovat.",
                tokenPath
            });
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Results.BadRequest(new
            {
                status = "not_configured",
                message = "Nastav OneDriveExport:OAuthClientId a OAuthClientSecret v appsettings.",
                howTo = new[]
                {
                    "1. Přejdi na https://portal.azure.com → App registrations → New registration",
                    "2. Supported account types: Personal Microsoft accounts",
                    "3. Redirect URI (Web): http://localhost:5001/api/auth/onedrive/callback",
                    "4. Po vytvoření: API permissions → Add → Microsoft Graph → Delegated → Files.ReadWrite + offline_access",
                    "5. Certificates & secrets → New client secret → zkopíruj Value",
                    "6. Nastav OneDriveExport:OAuthClientId = Application (client) ID",
                    "7. Nastav OneDriveExport:OAuthClientSecret = client secret Value"
                }
            });
        }

        var redirectUri = GetRedirectUri(ctx);
        var authUrl = $"{AuthBase}/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            $"&response_mode=query" +
            $"&prompt=consent";

        return Results.Ok(new
        {
            status = "authorization_required",
            message = "Otevři tuto URL v prohlížeči a přihlas se svým Microsoft účtem:",
            authorizationUrl = authUrl,
            note = "Po schválení tě Microsoft vrátí zpět a token se uloží automaticky."
        });
    }

    private static async Task<IResult> HandleCallback(
        string? code,
        string? error,
        string? error_description,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(error))
            return Results.BadRequest(new { error, error_description });

        if (string.IsNullOrEmpty(code))
            return Results.BadRequest(new { message = "Chybí parametr 'code'." });

        var (clientId, clientSecret, tokenPath) = GetConfig(configuration);
        var redirectUri = GetRedirectUri(ctx);

        try
        {
            var http = httpClientFactory.CreateClient("OneDriveToken");
            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["scope"] = Scopes
            });

            var resp = await http.PostAsync($"{AuthBase}/token", body);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return Results.BadRequest(new { status = "token_exchange_failed", detail = json });

            using var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
            var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() : null;

            if (string.IsNullOrEmpty(refreshToken))
                return Results.BadRequest(new
                {
                    message = "Microsoft nevrátil refresh_token. Zkontroluj scope offline_access a opakuj setup.",
                    accessToken = "OK (krátkodobý)"
                });

            var tokenJson = JsonSerializer.Serialize(new
            {
                client_id = clientId,
                client_secret = clientSecret,
                refresh_token = refreshToken,
                access_token = accessToken
            }, new JsonSerializerOptions { WriteIndented = true });

            var dir = Path.GetDirectoryName(tokenPath)!;
            Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(tokenPath, tokenJson);

            return Results.Ok(new
            {
                status = "success",
                message = "✅ OneDrive token úspěšně uložen.",
                tokenPath,
                nextStep = "Teď můžeš exportovat inzeráty: POST /api/listings/{id}/export-onedrive"
            });
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 500);
        }
    }

    private static (string clientId, string clientSecret, string tokenPath) GetConfig(IConfiguration cfg)
    {
        var clientId = cfg["OneDriveExport:OAuthClientId"] ?? "";
        var clientSecret = cfg["OneDriveExport:OAuthClientSecret"] ?? "";
        var tokenPath = cfg["OneDriveExport:UserTokenPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "secrets", "onedrive-token.json");
        return (clientId, clientSecret, tokenPath);
    }

    private static string GetRedirectUri(HttpContext ctx)
    {
        var req = ctx.Request;
        return $"{req.Scheme}://{req.Host}{CallbackPath}";
    }
}
