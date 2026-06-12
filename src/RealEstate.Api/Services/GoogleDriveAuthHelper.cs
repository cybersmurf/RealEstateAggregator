using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace RealEstate.Api.Services;

public static class GoogleDriveAuthHelper
{
    public const string ReauthorizePath = "/api/auth/drive/setup?force=true&redirect=true";

    public static async Task<DriveService> CreateDriveServiceFromTokenFileAsync(string tokenPath, CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(tokenPath, ct);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var clientId = root.GetProperty("client_id").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje client_id");
        var clientSecret = root.GetProperty("client_secret").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje client_secret");
        var refreshToken = root.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException($"'{tokenPath}' neobsahuje refresh_token");

        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [DriveService.Scope.Drive]
        });

        var userCredential = new UserCredential(flow, "user", new TokenResponse { RefreshToken = refreshToken });

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = userCredential,
            ApplicationName = "RealEstateAggregator"
        });
    }

    public static async Task<bool> IsTokenValidAsync(string tokenPath, CancellationToken ct)
    {
        try
        {
            var drive = await CreateDriveServiceFromTokenFileAsync(tokenPath, ct);
            var req = drive.Files.List();
            req.PageSize = 1;
            req.Fields = "files(id)";
            await req.ExecuteAsync(ct);
            return true;
        }
        catch (Exception ex) when (IsInvalidGrant(ex))
        {
            return false;
        }
    }

    public static bool IsInvalidGrant(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static (string clientId, string clientSecret) ReadOAuthCredentials(IConfiguration cfg, string tokenPath)
    {
        var clientId = cfg["GoogleDriveExport:OAuthClientId"];
        var clientSecret = cfg["GoogleDriveExport:OAuthClientSecret"];

        if ((string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            && File.Exists(tokenPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(tokenPath));
            clientId ??= doc.RootElement.GetProperty("client_id").GetString();
            clientSecret ??= doc.RootElement.GetProperty("client_secret").GetString();
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "GoogleDriveExport OAuth credentials chybí. Nastavte OAuthClientId/Secret nebo existující token soubor.");
        }

        return (clientId, clientSecret);
    }

    public static string BuildAuthorizationUrl(string clientId, string clientSecret, string redirectUri)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
            Scopes = [DriveService.Scope.Drive]
        });
        var request = (GoogleAuthorizationCodeRequestUrl)flow.CreateAuthorizationCodeRequest(redirectUri);
        request.AccessType = "offline";
        request.Prompt = "consent";
        return request.Build().AbsoluteUri;
    }
}
