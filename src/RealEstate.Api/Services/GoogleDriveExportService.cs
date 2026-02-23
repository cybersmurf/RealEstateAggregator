using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Export;
using RealEstate.Domain.Entities;
using RealEstate.Infrastructure;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace RealEstate.Api.Services;

public sealed class GoogleDriveExportService(
    RealEstateDbContext dbContext,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<GoogleDriveExportService> logger) : IGoogleDriveExportService
{
    public async Task<DriveExportResultDto> ExportListingToDriveAsync(Guid listingId, CancellationToken ct = default)
    {
        var listing = await dbContext.Listings
            .Include(l => l.Photos)
            .Include(l => l.Source)
            .FirstOrDefaultAsync(l => l.Id == listingId, ct)
            ?? throw new KeyNotFoundException($"Inzerát {listingId} nenalezen");

        var driveService = await CreateDriveServiceAsync();
        var rootFolderId = configuration["GoogleDriveExport:RootFolderId"]
            ?? throw new InvalidOperationException("GoogleDriveExport:RootFolderId není nakonfigurováno");

        // Vytvoříme složku s názvem inzerátu
        var folderName = SanitizeName($"{listing.Title} [{listing.LocationText}]");
        var folder = await CreateFolderAsync(driveService, folderName, rootFolderId, ct);

        logger.LogInformation("Vytvořena Drive složka: {Name} ({Id})", folderName, folder.Id);

        // INFO.md – přehled inzerátu v čitelné formě
        await UploadTextAsync(driveService, "INFO.md", BuildInfoMarkdown(listing), "text/markdown", folder.Id, ct);

        // DATA.json – strojově čitelná data
        await UploadTextAsync(driveService, "DATA.json", BuildDataJson(listing), "application/json", folder.Id, ct);

        // AI_INSTRUKCE.md – šablona pro konzultaci s AI
        await UploadTextAsync(driveService, "AI_INSTRUKCE.md", BuildAiInstructions(listing), "text/markdown", folder.Id, ct);

        // Fotky – stáhni z original_url a nahraj do podsložky Fotky/
        var photos = listing.Photos.OrderBy(p => p.Order).Take(20).ToList();
        if (photos.Count > 0)
        {
            var fotoFolder = await CreateFolderAsync(driveService, "Fotky_z_inzeratu", folder.Id, ct);
            await UploadPhotosAsync(driveService, photos, fotoFolder.Id, ct);
            logger.LogInformation("Nahráno {Count} fotek do Drive", photos.Count);
        }

        // Podsložka pro vlastní fotky z prohlídky (prázdná, připravená)
        await CreateFolderAsync(driveService, "Moje_fotky_z_prohlidky", folder.Id, ct);

        // Složku nastavíme jako "každý s odkazem může zobrazit"
        await SetPublicReadAsync(driveService, folder.Id, ct);

        var folderUrl = $"https://drive.google.com/drive/folders/{folder.Id}";
        logger.LogInformation("Export dokončen: {Url}", folderUrl);

        return new DriveExportResultDto(folderUrl, folderName, folder.Id);
    }

    // ── Google Drive helpers ────────────────────────────────────────────────

    private async Task<DriveService> CreateDriveServiceAsync()
    {
        var credPath = configuration["GoogleDriveExport:ServiceAccountCredentialsPath"]
            ?? throw new InvalidOperationException("GoogleDriveExport:ServiceAccountCredentialsPath není nakonfigurováno");

        var credJson = await System.IO.File.ReadAllTextAsync(credPath);
        var credential = GoogleCredential
            .FromJson(credJson)
            .CreateScoped(DriveService.Scope.Drive);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "RealEstateAggregator"
        });
    }

    private static async Task<DriveFile> CreateFolderAsync(DriveService drive, string name, string parentId, CancellationToken ct)
    {
        var folder = new DriveFile
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = [parentId]
        };
        var req = drive.Files.Create(folder);
        req.Fields = "id,name";
        return await req.ExecuteAsync(ct);
    }

    private static async Task UploadTextAsync(DriveService drive, string fileName, string content, string mimeType, string parentId, CancellationToken ct)
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var req = drive.Files.Create(meta, stream, mimeType);
        req.Fields = "id";
        await req.UploadAsync(ct);
    }

    private static async Task UploadBytesAsync(DriveService drive, string fileName, byte[] data, string parentId, CancellationToken ct)
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(data);
        var req = drive.Files.Create(meta, stream, "image/jpeg");
        req.Fields = "id";
        await req.UploadAsync(ct);
    }

    private static async Task SetPublicReadAsync(DriveService drive, string fileId, CancellationToken ct)
    {
        var perm = new Permission { Type = "anyone", Role = "reader" };
        await drive.Permissions.Create(perm, fileId).ExecuteAsync(ct);
    }

    private async Task UploadPhotosAsync(DriveService drive, List<ListingPhoto> photos, string folderId, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient("DrivePhotoDownload");
        http.Timeout = TimeSpan.FromSeconds(30);

        for (int i = 0; i < photos.Count; i++)
        {
            var url = photos[i].OriginalUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;

            try
            {
                var bytes = await http.GetByteArrayAsync(url, ct);
                var rawExt = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
                var ext = rawExt is ".jpg" or ".jpeg" or ".png" or ".webp" ? rawExt : ".jpg";
                var name = $"foto_{i + 1:D2}{ext}";
                await UploadBytesAsync(drive, name, bytes, folderId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Přeskočena fotka {Url}", url);
            }
        }
    }

    // ── Content builders ────────────────────────────────────────────────────

    private static string BuildInfoMarkdown(Listing l)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Title}");
        sb.AppendLine();
        sb.AppendLine($"> Exportováno: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Základní informace");
        sb.AppendLine();
        sb.AppendLine($"| Parametr | Hodnota |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| **Typ nemovitosti** | {l.PropertyType} |");
        sb.AppendLine($"| **Typ nabídky** | {l.OfferType} |");
        sb.AppendLine($"| **Cena** | {(l.Price.HasValue ? $"{l.Price.Value:N0} Kč" : "neuvedena")} {l.PriceNote} |");
        sb.AppendLine($"| **Lokalita** | {l.LocationText} |");
        if (!string.IsNullOrWhiteSpace(l.Municipality)) sb.AppendLine($"| **Obec** | {l.Municipality} |");
        if (!string.IsNullOrWhiteSpace(l.District)) sb.AppendLine($"| **Okres** | {l.District} |");
        if (!string.IsNullOrWhiteSpace(l.Region)) sb.AppendLine($"| **Kraj** | {l.Region} |");
        if (l.AreaBuiltUp.HasValue) sb.AppendLine($"| **Užitná plocha** | {l.AreaBuiltUp} m² |");
        if (l.AreaLand.HasValue) sb.AppendLine($"| **Plocha pozemku** | {l.AreaLand} m² |");
        if (l.Rooms.HasValue) sb.AppendLine($"| **Počet pokojů** | {l.Rooms} |");
        if (!string.IsNullOrWhiteSpace(l.ConstructionType)) sb.AppendLine($"| **Typ konstrukce** | {l.ConstructionType} |");
        if (!string.IsNullOrWhiteSpace(l.Condition)) sb.AppendLine($"| **Stav** | {l.Condition} |");
        sb.AppendLine($"| **Zdroj** | {l.SourceName} ({l.SourceCode}) |");
        sb.AppendLine($"| **URL inzerátu** | {l.Url} |");
        sb.AppendLine($"| **Poprvé viděno** | {l.FirstSeenAt:dd.MM.yyyy} |");
        sb.AppendLine();
        sb.AppendLine("## Popis");
        sb.AppendLine();
        sb.AppendLine(l.Description ?? "_Bez popisu_");
        sb.AppendLine();
        sb.AppendLine($"## Fotky");
        sb.AppendLine();
        sb.AppendLine($"Viz složka **Fotky_z_inzeratu/** ({l.Photos.Count} fotografií ze scrapu).");
        sb.AppendLine();
        sb.AppendLine("Vlastní fotky z prohlídky přidej do složky **Moje_fotky_z_prohlidky/**.");

        return sb.ToString();
    }

    private static string BuildDataJson(Listing l)
    {
        var data = new
        {
            id = l.Id,
            title = l.Title,
            property_type = l.PropertyType.ToString(),
            offer_type = l.OfferType.ToString(),
            price = l.Price,
            price_note = l.PriceNote,
            location_text = l.LocationText,
            municipality = l.Municipality,
            district = l.District,
            region = l.Region,
            area_built_up_m2 = l.AreaBuiltUp,
            area_land_m2 = l.AreaLand,
            rooms = l.Rooms,
            has_kitchen = l.HasKitchen,
            construction_type = l.ConstructionType,
            condition = l.Condition,
            source_name = l.SourceName,
            source_code = l.SourceCode,
            url = l.Url,
            description = l.Description,
            first_seen_at = l.FirstSeenAt,
            photos_count = l.Photos.Count,
            photo_urls = l.Photos.OrderBy(p => p.Order).Select(p => p.OriginalUrl).ToList()
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildAiInstructions(Listing l)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Instrukce pro AI analýzu nemovitosti");
        sb.AppendLine();
        sb.AppendLine($"**Inzerát:** {l.Title}");
        sb.AppendLine($"**Cena:** {(l.Price.HasValue ? $"{l.Price.Value:N0} Kč" : "neuveden")}");
        sb.AppendLine($"**Lokalita:** {l.LocationText}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Co tě žádám analyzovat");
        sb.AppendLine();
        sb.AppendLine("Prohlédni si fotky ve složce **Fotky_z_inzeratu/** a **Moje_fotky_z_prohlidky/**, přečti **INFO.md** a odpověz na tato témata:");
        sb.AppendLine();
        sb.AppendLine("### 1. Stav nemovitosti");
        sb.AppendLine("- Celkový vizuální stav (výborný / dobrý / zhoršený / špatný)");
        sb.AppendLine("- Stav podlah, zdí, stropu");
        sb.AppendLine("- Okna (plastová / dřevěná / stará)");
        sb.AppendLine("- Viditelné problémy: vlhkost, plísně, praskliny, zastaralé instalace");
        sb.AppendLine();
        sb.AppendLine("### 2. Rizika a červené vlajky");
        sb.AppendLine("- Skryté závady nebo potenciální problémy");
        sb.AppendLine("- Co si prověřit před koupí");
        sb.AppendLine("- Odhadované náklady na renovaci (optimisticky / realisticky)");
        sb.AppendLine();
        sb.AppendLine("### 3. Hodnocení ceny");
        sb.AppendLine($"- Je cena {(l.Price.HasValue ? $"{l.Price.Value:N0} Kč" : "?")} přiměřená pro tuto lokalitu a stav?");
        sb.AppendLine("- Odhad tržní hodnoty");
        sb.AppendLine("- Prostor pro vyjednávání");
        sb.AppendLine();
        sb.AppendLine("### 4. Výhody a silné stránky");
        sb.AppendLine("- Pozitiva z fotek a popisu");
        sb.AppendLine("- Co je na nemovitosti nejcennější");
        sb.AppendLine();
        sb.AppendLine("### 5. Celkové doporučení");
        sb.AppendLine("- **Doporučuji / Spíše ano / Neutrální / Spíše ne / Nedoporučuji**");
        sb.AppendLine("- Podmínky za jakých bych koupil/a");
        sb.AppendLine("- Prioritní akce před/po koupi");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Moje poznámky z prohlídky");
        sb.AppendLine();
        sb.AppendLine("_(Doplň vlastní postřehy před konzultací s AI)_");
        sb.AppendLine();
        sb.AppendLine("- Celkový dojem:");
        sb.AppendLine("- Co se mi líbilo:");
        sb.AppendLine("- Co mě znepokojilo:");
        sb.AppendLine("- Otázky na makléře:");
        sb.AppendLine("- Tvrzení makléře, která ověřit:");

        return sb.ToString();
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }
        var result = sb.ToString().Trim();
        return result.Length > 100 ? result[..100] : result;
    }
}
