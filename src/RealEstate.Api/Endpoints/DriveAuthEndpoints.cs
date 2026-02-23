using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Web;
using Google.Apis.Drive.v3;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// Jednorázové OAuth nastavení pro Google Drive.
///
/// Postup:
/// 1. GET  /api/auth/drive/setup  → vrátí URL kam přejít v prohlížeči
/// 2. Schválit přístup v Google → přesměruje na /api/auth/drive/callback?code=…
/// 3. Token se uloží do secrets/google-drive-token.json
/// 4. Export na Drive nyní funguje
/// </summary>
public static class DriveAuthEndpoints
{
    private const string CallbackPath = "/api/auth/drive/callback";

    public static IEndpointRouteBuilder MapDriveAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/drive/setup", SetupDriveAuth)
            .WithName("DriveAuthSetup")
            .WithSummary("Vrátí URL pro jednorázové OAuth povolení Drive přístupu");

        app.MapGet("/api/auth/drive/callback", HandleCallback)
            .WithName("DriveAuthCallback")
            .WithSummary("Zpracuje OAuth callback a uloží token");

        return app;
    }

    private static IResult SetupDriveAuth(IConfiguration configuration, HttpContext ctx)
    {
        var (clientId, clientSecret, tokenPath) = GetConfig(configuration);
        if (System.IO.File.Exists(tokenPath))
        {
            return Results.Ok(new
            {
                status = "already_configured",
                message = "Token již existuje. Export na Drive by měl fungovat.",
                tokenPath
            });
        }

        var redirectUri = GetRedirectUri(ctx);
        var flow = CreateFlow(clientId, clientSecret);
        var authUrl = flow.CreateAuthorizationCodeRequest(redirectUri).Build().AbsoluteUri;

        return Results.Ok(new
        {
            status = "authorization_required",
            message = "Otevři tuto URL v prohlížeči a přihlas se svým Google účtem:",
            authorizationUrl = authUrl,
            note = "Po schválení tě Google vrátí zpět a token se uloží automaticky."
        });
    }

    private static async Task<IResult> HandleCallback(
        string? code,
        string? error,
        IConfiguration configuration,
        HttpContext ctx)
    {
        if (!string.IsNullOrEmpty(error))
            return Results.BadRequest(new { error, message = "Google vrátil chybu." });

        if (string.IsNullOrEmpty(code))
            return Results.BadRequest(new { message = "Chybí parametr 'code'." });

        var (clientId, clientSecret, tokenPath) = GetConfig(configuration);
        var redirectUri = GetRedirectUri(ctx);
        var flow = CreateFlow(clientId, clientSecret);

        try
        {
            var tokenResponse = await flow.ExchangeCodeForTokenAsync("user", code, redirectUri, CancellationToken.None);

            if (string.IsNullOrEmpty(tokenResponse.RefreshToken))
                return Results.BadRequest(new
                {
                    message = "Google nevrátil refresh_token. Zkus revokovat přístup na https://myaccount.google.com/permissions a opakovat setup.",
                    accessToken = tokenResponse.AccessToken is not null ? "OK (krátkodobý)" : null
                });

            var tokenJson = JsonSerializer.Serialize(new
            {
                client_id = clientId,
                client_secret = clientSecret,
                refresh_token = tokenResponse.RefreshToken,
                access_token = tokenResponse.AccessToken,
                token_type = tokenResponse.TokenType
            }, new JsonSerializerOptions { WriteIndented = true });

            var dir = Path.GetDirectoryName(tokenPath)!;
            Directory.CreateDirectory(dir);
            await System.IO.File.WriteAllTextAsync(tokenPath, tokenJson);

            return Results.Ok(new
            {
                status = "success",
                message = "Token uložen! Export na Google Drive je připraven.",
                tokenPath
            });
        }
        catch (Exception ex)
        {
            return Results.Problem($"Chyba při výměně kódu za token: {ex.Message}");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (string clientId, string clientSecret, string tokenPath) GetConfig(IConfiguration cfg)
    {
        var clientId = cfg["GoogleDriveExport:OAuthClientId"]
            ?? throw new InvalidOperationException("GoogleDriveExport:OAuthClientId není nakonfigurováno.\nNastavte ho v appsettings.json nebo jako env proměnnou.");
        var clientSecret = cfg["GoogleDriveExport:OAuthClientSecret"]
            ?? throw new InvalidOperationException("GoogleDriveExport:OAuthClientSecret není nakonfigurováno.");
        var tokenPath = cfg["GoogleDriveExport:UserTokenPath"]
            ?? "secrets/google-drive-token.json";
        return (clientId, clientSecret, tokenPath);
    }

    private static string GetRedirectUri(HttpContext ctx)
    {
        var req = ctx.Request;
        return $"{req.Scheme}://{req.Host}{CallbackPath}";
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(string clientId, string clientSecret) =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [DriveService.Scope.Drive]
        });
}
