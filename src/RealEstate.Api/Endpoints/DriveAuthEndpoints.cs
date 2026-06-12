using System.Text.Json;
using Google.Apis.Auth.OAuth2.Flows;
using RealEstate.Api.Services;

namespace RealEstate.Api.Endpoints;

/// <summary>
/// OAuth nastavení pro Google Drive.
///
/// Postup:
/// 1. GET  /api/auth/drive/setup?force=true&amp;redirect=true  → přesměruje na Google login
/// 2. Schválit přístup → callback uloží secrets/google-drive-token.json
/// 3. Export na Drive funguje
/// </summary>
public static class DriveAuthEndpoints
{
    private const string CallbackPath = "/api/auth/drive/callback";

    public static IEndpointRouteBuilder MapDriveAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/drive/setup", SetupDriveAuth)
            .WithName("DriveAuthSetup")
            .WithSummary("Vrátí (nebo přesměruje na) URL pro OAuth povolení Drive přístupu");

        app.MapGet("/api/auth/drive/callback", HandleCallback)
            .WithName("DriveAuthCallback")
            .WithSummary("Zpracuje OAuth callback a uloží token");

        app.MapGet("/api/auth/drive/status", GetDriveAuthStatus)
            .WithName("DriveAuthStatus")
            .WithSummary("Ověří platnost uloženého OAuth tokenu");

        return app;
    }

    private static async Task<IResult> SetupDriveAuth(
        bool force,
        bool redirect,
        IConfiguration configuration,
        HttpContext ctx,
        CancellationToken ct)
    {
        var (clientId, clientSecret, tokenPath) = GetConfig(configuration);

        if (!force && File.Exists(tokenPath) && await GoogleDriveAuthHelper.IsTokenValidAsync(tokenPath, ct))
        {
            if (redirect)
                return Results.Redirect("/");

            return Results.Ok(new
            {
                status = "ok",
                message = "Google Drive token je platný. Export by měl fungovat.",
                tokenPath,
                reauthorizeUrl = GoogleDriveAuthHelper.ReauthorizePath
            });
        }

        var redirectUri = GetRedirectUri(ctx, configuration);
        var authUrl = GoogleDriveAuthHelper.BuildAuthorizationUrl(clientId, clientSecret, redirectUri);

        if (redirect)
            return Results.Redirect(authUrl);

        return Results.Ok(new
        {
            status = force ? "reauthorization_required" : "authorization_required",
            message = "Otevři authorizationUrl v prohlížeči a přihlas se Google účtem s Drive:",
            authorizationUrl = authUrl,
            reauthorizeUrl = GoogleDriveAuthHelper.ReauthorizePath,
            note = "Po schválení tě Google vrátí zpět a token se uloží automaticky."
        });
    }

    private static async Task<IResult> GetDriveAuthStatus(IConfiguration configuration, CancellationToken ct)
    {
        var (_, _, tokenPath) = GetConfig(configuration);
        if (!File.Exists(tokenPath))
        {
            return Results.Ok(new
            {
                status = "missing",
                message = "Google Drive token chybí. Spusťte OAuth setup.",
                reauthorizeUrl = GoogleDriveAuthHelper.ReauthorizePath
            });
        }

        var valid = await GoogleDriveAuthHelper.IsTokenValidAsync(tokenPath, ct);
        return Results.Ok(new
        {
            status = valid ? "ok" : "expired",
            message = valid
                ? "Token je platný."
                : "Token vypršel nebo byl zrušen. Je potřeba znovu autorizovat Google Drive.",
            reauthorizeUrl = GoogleDriveAuthHelper.ReauthorizePath
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
        var redirectUri = GetRedirectUri(ctx, configuration);
        var flow = CreateFlow(clientId, clientSecret);

        try
        {
            var tokenResponse = await flow.ExchangeCodeForTokenAsync("user", code, redirectUri, CancellationToken.None);

            if (string.IsNullOrEmpty(tokenResponse.RefreshToken))
                return Results.BadRequest(new
                {
                    message = "Google nevrátil refresh_token. Zkus revokovat přístup na https://myaccount.google.com/permissions a opakovat setup s ?force=true.",
                    reauthorizeUrl = GoogleDriveAuthHelper.ReauthorizePath
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
            await File.WriteAllTextAsync(tokenPath, tokenJson);

            return Results.Content(
                """
                <!DOCTYPE html><html lang="cs"><head><meta charset="utf-8"><title>Google Drive</title></head>
                <body style="font-family:sans-serif;max-width:600px;margin:2rem auto;padding:1rem">
                <h2>Google Drive připojen</h2>
                <p>Token byl uložen. Můžeš zavřít tuto stránku a znovu zkusit export inzerátu.</p>
                <p><a href="/">Zpět do aplikace</a></p>
                </body></html>
                """,
                "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            return Results.Problem($"Chyba při výměně kódu za token: {ex.Message}");
        }
    }

    private static (string clientId, string clientSecret, string tokenPath) GetConfig(IConfiguration cfg)
    {
        var tokenPath = cfg["GoogleDriveExport:UserTokenPath"] ?? "secrets/google-drive-token.json";
        var (clientId, clientSecret) = GoogleDriveAuthHelper.ReadOAuthCredentials(cfg, tokenPath);
        return (clientId, clientSecret, tokenPath);
    }

    private static string GetRedirectUri(HttpContext ctx, IConfiguration configuration)
    {
        var publicBase = configuration["GoogleDriveExport:PublicBaseUrl"]
            ?? configuration["PHOTOS_PUBLIC_BASE_URL"]
            ?? Environment.GetEnvironmentVariable("PUBLIC_API_URL");

        if (!string.IsNullOrWhiteSpace(publicBase)
            && Uri.TryCreate(publicBase.TrimEnd('/'), UriKind.Absolute, out var baseUri))
        {
            return $"{baseUri.Scheme}://{baseUri.Authority}{CallbackPath}";
        }

        var req = ctx.Request;
        var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
        return $"{scheme}://{req.Host}{CallbackPath}";
    }

    private static GoogleAuthorizationCodeFlow CreateFlow(string clientId, string clientSecret) =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new Google.Apis.Auth.OAuth2.ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [Google.Apis.Drive.v3.DriveService.Scope.Drive]
        });
}
