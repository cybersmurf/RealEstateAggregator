using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
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
        var infoId = await UploadTextAsync(driveService, "INFO.md", BuildInfoMarkdown(listing), "text/markdown", folder.Id, ct);
        await SetPublicReadAsync(driveService, infoId, ct);

        // DATA.json – strojově čitelná data
        var dataId = await UploadTextAsync(driveService, "DATA.json", BuildDataJson(listing), "application/json", folder.Id, ct);
        await SetPublicReadAsync(driveService, dataId, ct);

        // AI_INSTRUKCE.md – šablona pro konzultaci s AI
        var aiId = await UploadTextAsync(driveService, "AI_INSTRUKCE.md", BuildAiInstructions(listing), "text/markdown", folder.Id, ct);
        await SetPublicReadAsync(driveService, aiId, ct);

        // Fotky – stáhni z original_url a nahraj do podsložky Fotky/
        var photos = listing.Photos.OrderBy(p => p.Order).Take(20).ToList();
        if (photos.Count > 0)
        {
            var fotoFolder = await CreateFolderAsync(driveService, "Fotky_z_inzeratu", folder.Id, ct);
            await SetPublicReadAsync(driveService, fotoFolder.Id, ct);
            await UploadPhotosAsync(driveService, photos, fotoFolder.Id, ct);
            logger.LogInformation("Nahráno {Count} fotek do Drive", photos.Count);
        }

        // Podsložka pro vlastní fotky z prohlídky (prázdná, připravená)
        var myfotoFolder = await CreateFolderAsync(driveService, "Moje_fotky_z_prohlidky", folder.Id, ct);
        await SetPublicReadAsync(driveService, myfotoFolder.Id, ct);

        // Kořenovou složku nastavíme jako "každý s odkazem může zobrazit"
        await SetPublicReadAsync(driveService, folder.Id, ct);

        var folderUrl = $"https://drive.google.com/drive/folders/{folder.Id}";
        logger.LogInformation("Export dokončen: {Url}", folderUrl);

        return new DriveExportResultDto(folderUrl, folderName, folder.Id);
    }

    // ── Google Drive helpers ────────────────────────────────────────────────

    private async Task<DriveService> CreateDriveServiceAsync()
    {
        // Preferujeme OAuth UserToken (soubory vlastní uživatel, má storage quota)
        var tokenPath = configuration["GoogleDriveExport:UserTokenPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "secrets", "google-drive-token.json");

        if (System.IO.File.Exists(tokenPath))
        {
            logger.LogInformation("Drive auth: OAuth UserCredential z {Path}", tokenPath);
            return await CreateDriveServiceFromTokenAsync(tokenPath);
        }

        // Fallback: Service Account
        logger.LogWarning("Drive auth: OAuth token nenalezen ({Path}), používám Service Account – upload souborů může selhat kvůli kvótě", tokenPath);
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

    /// <summary>
    /// Vytvoří DriveService z uloženého OAuth tokenu.
    /// Soubor musí mít formát: { "client_id": "...", "client_secret": "...", "refresh_token": "..." }
    /// Získáš ho přes Google OAuth Playground: https://developers.google.com/oauthplayground/
    /// </summary>
    private static async Task<DriveService> CreateDriveServiceFromTokenAsync(string tokenPath)
    {
        var raw = await System.IO.File.ReadAllTextAsync(tokenPath);
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

        var tokenResponse = new TokenResponse { RefreshToken = refreshToken };
        var userCredential = new UserCredential(flow, "user", tokenResponse);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = userCredential,
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

    private static async Task<string> UploadTextAsync(DriveService drive, string fileName, string content, string mimeType, string parentId, CancellationToken ct)
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var req = drive.Files.Create(meta, stream, mimeType);
        req.Fields = "id";
        var result = await req.UploadAsync(ct);
        if (result.Status == Google.Apis.Upload.UploadStatus.Failed)
            throw new InvalidOperationException($"Upload souboru '{fileName}' selhal: {result.Exception?.Message}", result.Exception);
        return req.ResponseBody!.Id;
    }

    private static async Task<string> UploadBytesAsync(DriveService drive, string fileName, byte[] data, string parentId, CancellationToken ct)
    {
        var meta = new DriveFile { Name = fileName, Parents = [parentId] };
        using var stream = new MemoryStream(data);
        var req = drive.Files.Create(meta, stream, "image/jpeg");
        req.Fields = "id";
        var result = await req.UploadAsync(ct);
        if (result.Status == Google.Apis.Upload.UploadStatus.Failed)
            throw new InvalidOperationException($"Upload fotky '{fileName}' selhal: {result.Exception?.Message}", result.Exception);
        return req.ResponseBody!.Id;
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
                await UploadPhotoBytesWithPermAsync(drive, name, bytes, folderId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Přeskočena fotka {Url}", url);
            }
        }
    }

    private async Task UploadPhotoBytesWithPermAsync(DriveService drive, string fileName, byte[] data, string parentId, CancellationToken ct)
    {
        var fileId = await UploadBytesAsync(drive, fileName, data, parentId, ct);
        await SetPublicReadAsync(drive, fileId, ct);
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
        sb.AppendLine($"| **URL inzerátu** | [{l.Url}]({l.Url}) |");
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
            photo_urls = l.Photos.OrderBy(p => p.Order).Select(p => p.OriginalUrl).ToList(),
            age_category = IsNewBuild(l.Condition, l.Description) ? "new_build" : "existing"
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool IsNewBuild(string? condition, string? description)
    {
        var haystack = $"{condition} {description}".ToLowerInvariant();
        return haystack.Contains("novostavb") ||
               haystack.Contains("ve výstavb") ||
               haystack.Contains("ve vystavb") ||
               haystack.Contains("pod klíč") ||
               haystack.Contains("pod klic") ||
               haystack.Contains("developerský projekt") ||
               haystack.Contains("developersky projekt") ||
               haystack.Contains("dokončení 202") ||
               haystack.Contains("dokonceni 202") ||
               condition?.ToLowerInvariant().Contains("nový") == true ||
               condition?.ToLowerInvariant().Contains("nová") == true;
    }

    private static string BuildAiInstructions(Listing l)
    {
        var price = l.Price.HasValue ? $"{l.Price.Value:N0} Kč" : "neuvedena";
        var area = l.AreaBuiltUp.HasValue
            ? $"{l.AreaBuiltUp} m² užitná" + (l.AreaLand.HasValue ? $" / {l.AreaLand} m² pozemek" : "")
            : (l.AreaLand.HasValue ? $"{l.AreaLand} m² pozemek" : "neuvedena");
        var isNewBuild = IsNewBuild(l.Condition, l.Description);

        var sb = new StringBuilder();
        sb.AppendLine("# Instrukce pro AI analýzu nemovitosti");
        sb.AppendLine();
        sb.AppendLine("## ZÁKLADNÍ ÚDAJE");
        sb.AppendLine();
        sb.AppendLine($"**Adresa / lokalita:** {l.LocationText}" +
            (!string.IsNullOrWhiteSpace(l.Municipality) ? $", {l.Municipality}" : "") +
            (!string.IsNullOrWhiteSpace(l.District) ? $", okres {l.District}" : "") +
            (!string.IsNullOrWhiteSpace(l.Region) ? $", {l.Region}" : ""));
        sb.AppendLine($"**Typ:** {l.PropertyType} / {l.OfferType}");
        sb.AppendLine($"**Nabídková cena:** {price}" + (!string.IsNullOrWhiteSpace(l.PriceNote) ? $" ({l.PriceNote})" : ""));
        sb.AppendLine($"**Plocha:** {area}");
        if (l.Rooms.HasValue) sb.AppendLine($"**Počet pokojů:** {l.Rooms}");
        if (!string.IsNullOrWhiteSpace(l.ConstructionType)) sb.AppendLine($"**Typ konstrukce:** {l.ConstructionType}");
        if (!string.IsNullOrWhiteSpace(l.Condition)) sb.AppendLine($"**Stav dle inzerátu:** {l.Condition}");
        sb.AppendLine(isNewBuild
            ? "**Kategorie stáří:** 🆕 NOVOSTAVBA / VE VÝSTAVBĚ"
            : "**Kategorie stáří:** existující nemovitost");
        sb.AppendLine($"**Zdroj inzerátu:** {l.SourceName} ({l.SourceCode})");
        sb.AppendLine($"**URL:** [{l.Url}]({l.Url})");
        sb.AppendLine($"**Datum scrapu:** {l.FirstSeenAt:dd.MM.yyyy}");
        sb.AppendLine($"**Datum prohlídky:** _(doplň)_");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## STRUKTURA SLOŽKY");
        sb.AppendLine();
        sb.AppendLine("- `AI_INSTRUKCE.md` – tento soubor s instrukcemi a základními údaji");
        sb.AppendLine("- `INFO.md` – přehled všech parametrů a popis z inzerátu");
        sb.AppendLine("- `DATA.json` – strojově čitelná data");
        sb.AppendLine("- `Fotky_z_inzeratu/` – fotky stažené ze scrapu");
        sb.AppendLine("- `Moje_fotky_z_prohlidky/` – **sem nahraj vlastní fotky z prohlídky**");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## ÚKOL PRO AI");
        sb.AppendLine();
        sb.AppendLine("Prohlédni si fotky ve složkách `Fotky_z_inzeratu/` a `Moje_fotky_z_prohlidky/`,");
        sb.AppendLine("přečti `INFO.md` a `DATA.json` a proveď **komplexní analýzu této nemovitosti** z pohledu potenciálního kupce/investora.");
        sb.AppendLine();
        if (isNewBuild)
        {
            sb.AppendLine("> ⚠️ **DŮLEŽITÉ:** Tato nemovitost je **NOVOSTAVBA nebo stavba ve výstavbě**.");
            sb.AppendLine("> - **NEPIŠ nic o rekonstrukci, opotřebení, zastaralých instalacích ani nutnosti oprav.**");
            sb.AppendLine("> - Počítej s tím, že stavba má životnost 80–100 let a v prvních 10–15 letech se řeší pouze běžná údržba (fasáda, těsnění oken, apod.).");
            sb.AppendLine("> - Místo hodnocení stavu hodnoť **kvalitu projektu, developera, termín dokončení a smluvní záruky.**");
            sb.AppendLine();
        }
        sb.AppendLine("Zaměř se na:");
        sb.AppendLine();

        if (isNewBuild)
        {
            sb.AppendLine("### 1. KVALITA PROJEKTU A DEVELOPERA");
            sb.AppendLine("- Hodnocení developera: zkušenosti, reference, dokončené projekty, insolvence");
            sb.AppendLine("- Termín dokončení – je realistický? Existují sankce za prodlení?");
            sb.AppendLine("- Záruční doba a záruky za vady (zákonem min. 3 roky, ideálně více)");
            sb.AppendLine("- Kvalita použitých materiálů a standardu vybavení (dle inzerátu/vizualizací)");
            sb.AppendLine("- Jsou k dispozici půdorysy, technická dokumentace, energetický průkaz?");
        }
        else
        {
            sb.AppendLine("### 1. ANALÝZA STAVU A KVALITY");
            sb.AppendLine("- Posouzení stavu nemovitosti podle fotografií");
            sb.AppendLine("- Identifikace viditelných problémů (vlhkost, praskliny, špatné opravy, zastaralé instalace)");
            sb.AppendLine("- Odhadovaný rozsah nutných oprav a rekonstrukce – rozlišuj: **nutné ihned / do 5 let / dlouhodobé**");
            sb.AppendLine("- Porovnání stavu uvedeného v inzerátu vs. realita na fotkách");
        }
        sb.AppendLine();
        sb.AppendLine("### 2. HODNOCENÍ CENY");
        sb.AppendLine($"- Je nabídková cena **{price}** adekvátní vzhledem ke stavu a lokalitě?");
        sb.AppendLine("- Odhad reálné tržní hodnoty");
        sb.AppendLine("- Potenciál pro vyjednávání (doporučená nabídková cena)");
        if (isNewBuild)
        {
            sb.AppendLine("- Porovnání ceny za m² s podobnými novostavbami v regionu");
            sb.AppendLine("- Výhody/nevýhody koupě před dokončením (cena vs. riziko)");
            sb.AppendLine("- **Investiční výnos:** odhad nájmu po dokončení, hrubý yield, cashflow");
        }
        else
        {
            sb.AppendLine("- Výpočet nákladů na nutné opravy (ihned + do 5 let)");
            sb.AppendLine("- **Všechna čísla uváděj jako: cena pořízení + náklady na opravy = celková investice**");
            sb.AppendLine("- **ROI analýza** pokud investor (pronájem vs. prodej po renovaci)");
        }
        sb.AppendLine();
        sb.AppendLine("### 3. LOKACE A OKOLÍ");
        sb.AppendLine("- Kvalita lokality (dostupnost služeb, doprava, infrastruktura)");
        sb.AppendLine("- Potenciální růst/pokles hodnoty v oblasti");
        sb.AppendLine("- Rizika lokality (průmyslová zóna, hluk, povodně)");
        sb.AppendLine("- Parkování, přístup, orientace ke světovým stranám");
        sb.AppendLine();
        if (isNewBuild)
        {
            sb.AppendLine("### 4. TECHNICKÁ SPECIFIKACE (dle dokumentace)");
            sb.AppendLine("- **Konstrukční systém** – zděný / dřevostavba / panel / jiný");
            sb.AppendLine("- **Zateplení a energetická třída** – A/B/C, tepelná čerpadla, solární panely");
            sb.AppendLine("- **Topení** – typ systému, zdroj energie (plyn / TČ / elektřina)");
            sb.AppendLine("- **Podlahy a okna** – materiály a standard dle inzerátu");
            sb.AppendLine("- **Parkování / garáž** – zahrnuty v ceně nebo příplatek?");
            sb.AppendLine("- **Sklep / předzahrádka / terasa** – co je v ceně?");
            sb.AppendLine("- ⚠️ Neposuzuj degradaci ani opotřebení – nemovitost je nová.");
        }
        else
        {
            sb.AppendLine("### 4. TECHNICKÝ STAV (podle fotek)");
            sb.AppendLine("- **Střecha** – typ, stav, odhadované stáří, nutnost výměny");
            sb.AppendLine("- **Fasáda** – stav, nutnost zateplení (odhadované náklady)");
            sb.AppendLine("- **Okna** – materiál, těsnost, tepelné ztráty");
            sb.AppendLine("- **Instalace** – elektřina (rozvody, pojistky), plyn, voda, kanalizace");
            sb.AppendLine("- **Topení** – typ systému, stáří, účinnost");
            sb.AppendLine("- **Podlahy** – materiál, stav");
            sb.AppendLine("- **Vlhkost** – známky zatékání, plísně, špatné odvětrání");
        }
        sb.AppendLine();
        sb.AppendLine("### 5. DISPOZICE A VYUŽITELNOST");
        sb.AppendLine("- Funkčnost půdorysu");
        sb.AppendLine("- Potenciál pro úpravy (bourání/přidání příček)");
        sb.AppendLine("- Světlost místností");
        sb.AppendLine("- Skladovací prostory");
        sb.AppendLine(isNewBuild
            ? "- Možnost úprav standardu v rámci developer. procesu (kuchyňská linka, obklady, apod.)"
            : "- Potenciál podkroví/půdy/sklepa");
        sb.AppendLine();
        sb.AppendLine("### 6. RIZIKA A RED FLAGS");
        sb.AppendLine("- Seznam všech identifikovaných rizik");
        if (isNewBuild)
        {
            sb.AppendLine("- Riziko nedokončení / insolvence developera – jak je ošetřeno?");
            sb.AppendLine("- Vinkulace kupní ceny (notářská úschova / bankovní akreditiv)");
            sb.AppendLine("- Smlouva o smlouvě budoucí – jaké jsou sankce a exit klauzule?");
            sb.AppendLine("- Změny projektu bez souhlasu kupujícího");
            sb.AppendLine("- Problémy s územním povolením nebo stavebním řízením");
        }
        else
        {
            sb.AppendLine("- Kritické body vyžadující prohlídku specialistou (statik, elektrikář)");
            sb.AppendLine("- Možné skryté náklady");
            sb.AppendLine("- Právní rizika (rekonstrukce bez povolení, věcná břemena)");
        }
        sb.AppendLine();
        sb.AppendLine("### 7. INVESTIČNÍ ANALÝZA");
        if (isNewBuild)
        {
            sb.AppendLine("- **Vstupní investice:** kupní cena + notář + daně + vybavení");
            sb.AppendLine("- **Odhad nájmu po dokončení** (Kč/měsíc) – long-term / Airbnb");
            sb.AppendLine("- **Hrubý yield** = roční nájem / kupní cena × 100");
            sb.AppendLine("- **Čistý cashflow:** nájem – hypotéka – správa – fond oprav – pojištění");
            sb.AppendLine("- **Citlivostní analýza:** co když obsazenost klesne na 80 %? Co když naroste sazba?");
            sb.AppendLine("- Očekávaný růst hodnoty nemovitosti (lokalita, developmentový trend)");
        }
        else
        {
            sb.AppendLine("- **Celková investice:** kupní cena + nutné opravy ihned + opravy do 5 let");
            sb.AppendLine("- Odhad tržní hodnoty po opravách");
            sb.AppendLine("- Potenciální výnos z pronájmu (Kč/měsíc)");
            sb.AppendLine("- **Hrubý yield** = roční nájem / celková investice × 100");
            sb.AppendLine("- Break-even a návratnost investice");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("### 8. POROVNÁNÍ S TRHEM");
        sb.AppendLine("- Jak si stojí cena vůči podobným nemovitostem v oblasti");
        sb.AppendLine(isNewBuild
            ? "- Benchmark s jinými novostavbami v regionu (cena/m², standard, termín)"
            : "- Benchmark s inzeráty stejného typu/lokality");
        sb.AppendLine();
        sb.AppendLine("### 9. DOPORUČENÍ");
        sb.AppendLine("- **Koupit / Nekoupit / Vyjednávat**");
        sb.AppendLine("- Maximální rozumná nabídková cena");
        sb.AppendLine("- Priority pro vyjednávání");
        sb.AppendLine("- Co prověřit při prohlídce/před podpisem");
        sb.AppendLine("- Dodatečné expertní posudky (statik, energetický specialista)");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## POZNÁMKY Z PROHLÍDKY _(vyplň ručně)_");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("Celkový dojem:");
        sb.AppendLine("Co se mi líbilo:");
        sb.AppendLine("Co mě znepokojilo:");
        sb.AppendLine("Co říkal makléř/prodejce:");
        sb.AppendLine("Nesrovnalosti mezi inzerátem a realitou:");
        sb.AppendLine("Vůně, sousedé, okolí:");
        sb.AppendLine("Měření / rozměry:");
        sb.AppendLine("Věci k prověření:");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## DOPLŇUJÍCÍ KONTEXT _(vyplň před odesláním AI)_");
        sb.AppendLine();
        sb.AppendLine("**Můj rozpočet:** _(max cena včetně rekonstrukce)_");
        sb.AppendLine("**Účel:** _(vlastní bydlení / investice / pronájem)_");
        sb.AppendLine("**Timeline:** _(jak rychle potřebuji koupit)_");
        sb.AppendLine("**Tolerance rizika:** _(ochota riskovat / preferuji jistotu)_");
        sb.AppendLine("**DIY skills:** _(dělám si sám / najmu řemeslníky)_");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## POŽADOVANÝ VÝSTUP");
        sb.AppendLine();
        sb.AppendLine("Vytvoř **strukturovanou analýzu v Markdown** s:");
        sb.AppendLine("- Executive summary (3–5 vět)");
        sb.AppendLine("- Detailní rozbor podle bodů výše");
        sb.AppendLine("- **Tabulka nákladů** (koupě + renovace + poplatky)");
        sb.AppendLine("- **Risk matrix** (vysoké / střední / nízké riziko)");
        sb.AppendLine("- **Jasné doporučení** s odůvodněním");
        sb.AppendLine();
        sb.AppendLine("_Formát: Markdown dokument připravený k copy-paste nebo exportu do PDF._");

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
