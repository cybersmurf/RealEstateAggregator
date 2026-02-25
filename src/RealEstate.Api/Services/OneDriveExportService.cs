using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Export;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;

namespace RealEstate.Api.Services;

/// <summary>
/// Export inzerátu na Microsoft OneDrive přes Microsoft Graph REST API.
/// Nevyžaduje žádný extra NuGet balíček – pouze System.Net.Http.
///
/// Výhoda oproti Google Drive: sdílení se DĚDÍ – stačí nasdílet root složku
/// a vše uvnitř je veřejně dostupné bez dalšího nastavování.
/// </summary>
public sealed class OneDriveExportService(
    RealEstateDbContext dbContext,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<OneDriveExportService> logger) : IOneDriveExportService
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    public async Task<DriveExportResultDto> ExportListingAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await dbContext.Listings
            .Include(l => l.Photos)
            .Include(l => l.Source)
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        // ── IDEMPOTENCE: pokud jsme už exportovali, vrátíme existující složku ──
        if (!string.IsNullOrWhiteSpace(listing.OneDriveFolderId))
        {
            var existingName = ListingExportContentBuilder.SanitizeName($"{listing.Title} [{listing.LocationText}]");
            var existingShare = await GetOrCreateSharingLinkAsync(listing.OneDriveFolderId, ct);
            var existingTotal = listing.Photos.Count;
            logger.LogInformation("OneDrive export: vracím existující složku {Id}", listing.OneDriveFolderId);
            return new DriveExportResultDto(existingShare, existingName, listing.OneDriveFolderId, listing.OneDriveInspectionFolderId,
                PhotosTotal: existingTotal);
        }

        var accessToken = await GetAccessTokenAsync(ct);
        var http = CreateGraphClient(accessToken);
        var rootId = configuration["OneDriveExport:RootFolderId"];

        var folderName = ListingExportContentBuilder.SanitizeName($"{listing.Title} [{listing.LocationText}]");
        var folderId = await CreateFolderAsync(http, folderName, rootId, ct);
        logger.LogInformation("Vytvořena OneDrive složka: {Name} ({Id})", folderName, folderId);

        var photos = listing.Photos.OrderBy(p => p.Order).Take(20).ToList();
        var photoLinks = new List<PhotoLink>();
        if (photos.Count > 0)
        {
            var fotoFolderId = await CreateFolderAsync(http, "Fotky_z_inzeratu", folderId, ct);
            photoLinks = await UploadPhotosAsync(http, photos, fotoFolderId, ct);
            if (photoLinks.Count < photos.Count)
                logger.LogWarning("OneDrive export: nahráno pouze {Uploaded}/{Total} fotek – {Skipped} fotek se nepodařilo stáhnout",
                    photoLinks.Count, photos.Count, photos.Count - photoLinks.Count);
            else
                logger.LogInformation("Nahráno {Count}/{Total} fotek na OneDrive", photoLinks.Count, photos.Count);
        }

        var shareUrl = await CreateSharingLinkAsync(http, folderId, ct);

        await UploadTextAsync(http, "INFO.md",
            ListingExportContentBuilder.BuildInfoMarkdown(listing, photoLinks), folderId, ct);
        await UploadTextAsync(http, "DATA.json",
            ListingExportContentBuilder.BuildDataJson(listing), folderId, ct);
        if (photoLinks.Count > 0)
            await UploadTextAsync(http, "FOTKY_LINKY.md",
                ListingExportContentBuilder.BuildPhotoLinksMarkdown(listing, photoLinks), folderId, ct);
        await UploadTextAsync(http, "AI_INSTRUKCE.md",
            ListingExportContentBuilder.BuildAiInstructions(listing, photoLinks, shareUrl), folderId, ct);

        var inspectionFolderId = await CreateFolderAsync(http, "Moje_fotky_z_prohlidky", folderId, ct);

        // ── Uložíme folder IDs do DB ──────────────────────────────────────────
        listing.OneDriveFolderId = folderId;
        listing.OneDriveInspectionFolderId = inspectionFolderId;
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("OneDrive export dokončen a folder IDs uloženy do DB: {Url} [{Uploaded}/{Total} fotek]",
            shareUrl, photoLinks.Count, photos.Count);
        return new DriveExportResultDto(shareUrl, folderName, folderId, inspectionFolderId,
            PhotosUploaded: photoLinks.Count, PhotosTotal: photos.Count);
    }

    private async Task<string> GetOrCreateSharingLinkAsync(string folderId, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var http = CreateGraphClient(accessToken);
        return await CreateSharingLinkAsync(http, folderId, ct);
    }

    public async Task SaveAnalysisAsync(string folderId, string content, CancellationToken ct = default)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var http = CreateGraphClient(accessToken);
        var fileName = $"ANALYZA_{DateTime.Now:yyyyMMdd_HHmm}.md";
        await UploadTextAsync(http, fileName, content, folderId, ct);
        logger.LogInformation("Uložena analýza do OneDrive složky {FolderId} jako {File}", folderId, fileName);
    }

    public async Task UploadInspectionPhotosAsync(
        string inspectionFolderId,
        IReadOnlyList<(string Name, byte[] Data, string ContentType)> files,
        CancellationToken ct = default)
    {
        var accessToken = await GetAccessTokenAsync(ct);
        var http = CreateGraphClient(accessToken);
        foreach (var (name, data, contentType) in files)
        {
            var url = $"{GraphBase}/me/drive/items/{inspectionFolderId}:/{Uri.EscapeDataString(name)}:/content";
            var req = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(data)
            };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            logger.LogInformation("Nahrána fotka z prohlídky na OneDrive: {Name}", name);
        }
    }

    // ── OneDrive / Graph helpers ────────────────────────────────────────────

    private HttpClient CreateGraphClient(string accessToken)
    {
        var http = httpClientFactory.CreateClient("OneDriveGraph");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    /// <summary>Načte platný access token. Pokud je prošlý, automaticky ho refreshuje a uloží.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var tokenPath = configuration["OneDriveExport:UserTokenPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "secrets", "onedrive-token.json");

        if (!System.IO.File.Exists(tokenPath))
            throw new InvalidOperationException(
                $"OneDrive token nenalezen v '{tokenPath}'. Spusť setup: GET /api/auth/onedrive/setup");

        var raw = await System.IO.File.ReadAllTextAsync(tokenPath, ct);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // Pokud máme platný access token (nevíme přesně expiry, ale zkusíme refresh vždy)
        var refreshToken = root.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException("onedrive-token.json neobsahuje refresh_token");
        var clientId = root.GetProperty("client_id").GetString()!;
        var clientSecret = root.GetProperty("client_secret").GetString()!;

        return await RefreshAccessTokenAsync(clientId, clientSecret, refreshToken, tokenPath, ct);
    }

    private async Task<string> RefreshAccessTokenAsync(
        string clientId, string clientSecret, string refreshToken, string tokenPath, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("OneDriveToken");
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
            ["scope"] = "Files.ReadWrite offline_access"
        });

        var resp = await http.PostAsync(TokenEndpoint, body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token refresh selhal ({resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var newRefresh = doc.RootElement.TryGetProperty("refresh_token", out var rtProp)
            ? rtProp.GetString() ?? refreshToken
            : refreshToken;

        // Uložíme aktualizovaný token
        var updated = JsonSerializer.Serialize(new
        {
            client_id = clientId,
            client_secret = clientSecret,
            refresh_token = newRefresh,
            access_token = accessToken
        }, new JsonSerializerOptions { WriteIndented = true });

        await System.IO.File.WriteAllTextAsync(tokenPath, updated, ct);
        logger.LogInformation("OneDrive access token refreshnut a uložen");
        return accessToken;
    }

    private static async Task<string> CreateFolderAsync(
        HttpClient http, string name, string? parentId, CancellationToken ct)
    {
        // Endpoint: POST /me/drive/items/{parentId}/children  nebo /me/drive/root/children
        var endpoint = string.IsNullOrWhiteSpace(parentId)
            ? $"{GraphBase}/me/drive/root/children"
            : $"{GraphBase}/me/drive/items/{parentId}/children";

        var payload = JsonSerializer.Serialize(new
        {
            name,
            folder = new { },
            // @microsoft.graph.conflictBehavior = "rename" → při konfliktu přidá číslo
            conflictBehavior = "rename"
        });

        var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> UploadTextAsync(
        HttpClient http, string fileName, string content, string parentId, CancellationToken ct)
    {
        // PUT /me/drive/items/{parentId}:/{fileName}:/content
        var url = $"{GraphBase}/me/drive/items/{parentId}:/{Uri.EscapeDataString(fileName)}:/content";
        var bytes = Encoding.UTF8.GetBytes(content);
        var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(bytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<List<PhotoLink>> UploadPhotosAsync(
        HttpClient graphHttp, List<ListingPhoto> photos, string folderId, CancellationToken ct)
    {
        var result = new List<PhotoLink>();
        var dl = httpClientFactory.CreateClient("OneDrivePhotoDownload");
        dl.Timeout = TimeSpan.FromSeconds(30);

        for (int i = 0; i < photos.Count; i++)
        {
            var url = photos[i].OriginalUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            try
            {
                // Retry 3× s exponenciálním backoffem pro nestabilní CDN
                byte[] bytes = null!;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try { bytes = await dl.GetByteArrayAsync(url, ct); break; }
                    catch (Exception ex) when (attempt < 3)
                    {
                        logger.LogWarning("Stahování fotky {Url} pokus {A}/3: {Msg}", url, attempt, ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    }
                }
                var rawExt = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
                var ext = rawExt is ".jpg" or ".jpeg" or ".png" or ".webp" ? rawExt : ".jpg";
                var name = $"foto_{i + 1:D2}{ext}";

                var uploadUrl = $"{GraphBase}/me/drive/items/{folderId}:/{Uri.EscapeDataString(name)}:/content";
                var req = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(bytes)
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                var resp = await graphHttp.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(body);
                var itemId = doc.RootElement.GetProperty("id").GetString()!;

                // @microsoft.graph.downloadUrl = přímý CDN odkaz bez JS přesměrování (platí několik hodin)
                // Sharing link vrací HTML preview stránku – AI nástroje ho neumí zobrazit
                string directUrl;
                if (doc.RootElement.TryGetProperty("@microsoft.graph.downloadUrl", out var cdnProp))
                    directUrl = cdnProp.GetString()!;
                else
                    directUrl = await CreateSharingLinkAsync(graphHttp, itemId, ct);

                // OriginalSourceUrl = originální URL ze scraperu – trvalá přímá image URL pro AI nástroje
                result.Add(new PhotoLink(name, directUrl, url));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Přeskočena fotka #{Idx} {Url}", i + 1, url);
            }
        }
        return result;
    }

    /// <summary>
    /// Vytvoří anonymní view odkaz na soubor/složku přes Graph API.
    /// Vrátí webUrl, která je veřejně přístupná bez přihlášení.
    /// </summary>
    private static async Task<string> CreateSharingLinkAsync(
        HttpClient http, string itemId, CancellationToken ct)
    {
        var url = $"{GraphBase}/me/drive/items/{itemId}/createLink";
        var payload = JsonSerializer.Serialize(new
        {
            type = "view",
            scope = "anonymous"
        });
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var resp = await http.SendAsync(req, ct);

        // 200 = nový link, 409 = link již existuje (vrátí existující)
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Zkusíme získat webUrl z existujícího
            var conflictBody = await resp.Content.ReadAsStringAsync(ct);
            using var cd = JsonDocument.Parse(conflictBody);
            if (cd.RootElement.TryGetProperty("link", out var cl) &&
                cl.TryGetProperty("webUrl", out var wu))
                return wu.GetString()!;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("link").GetProperty("webUrl").GetString()!;
    }
}
