using System.Text;
using System.Text.Json;
using RealEstate.Domain.Entities;

namespace RealEstate.Api.Services;

/// <summary>Přímý odkaz na nahraný soubor (fotku) v cloudovém úložišti.</summary>
/// <param name="Name">Název souboru (foto_01.jpg…)</param>
/// <param name="DirectUrl">Přímá URL v OneDrive (CDN, bez JS přesměrování)</param>
/// <param name="OriginalSourceUrl">Původní URL ze scraperu – funguje pro AI nástroje (Perplexity, Claude…)</param>
public record PhotoLink(string Name, string DirectUrl, string OriginalSourceUrl = "");

/// <summary>
/// Sdílené content buildery pro export inzerátu – používají GoogleDriveExportService i OneDriveExportService.
/// </summary>
public static class ListingExportContentBuilder
{
    public static string BuildPhotoLinksMarkdown(Listing l, IReadOnlyList<PhotoLink> photos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Fotky z inzerátu – přímé odkazy");
        sb.AppendLine();
        sb.AppendLine($"> Inzerát: **{l.Title}** – {l.LocationText}");
        sb.AppendLine($"> Exportováno: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Tento soubor obsahuje **přímé URL na každou fotku** pro AI nástroje (Perplexity, Claude, ChatGPT…)");
        sb.AppendLine();
        sb.AppendLine("> ℹ️ **Jak používat:** Zkopíruj blok URL níže a vlož ho přímo do chatu s AI nástrojem.");
        sb.AppendLine();
        sb.AppendLine("## Přímé URL fotek (ze zdroje inzerátu)");
        sb.AppendLine();
        sb.AppendLine("> Tyto URL jsou přímé image linky – fungují v Perplexity, Claude i ChatGPT.");
        sb.AppendLine();
        foreach (var p in photos)
        {
            var aiUrl = !string.IsNullOrEmpty(p.OriginalSourceUrl) ? p.OriginalSourceUrl : p.DirectUrl;
            sb.AppendLine($"- [{p.Name}]({aiUrl})");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Blok pro vložení do AI chatu");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var p in photos)
        {
            var aiUrl = !string.IsNullOrEmpty(p.OriginalSourceUrl) ? p.OriginalSourceUrl : p.DirectUrl;
            sb.AppendLine(aiUrl);
        }
        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string BuildInfoMarkdown(Listing l, IReadOnlyList<PhotoLink>? photos = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Title}");
        sb.AppendLine();
        sb.AppendLine($"> Exportováno: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine("## Základní informace");
        sb.AppendLine();
        sb.AppendLine("| Parametr | Hodnota |");
        sb.AppendLine("|---|---|");
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
        sb.AppendLine("## Fotky");
        sb.AppendLine();
        if (photos is { Count: > 0 })
        {
            sb.AppendLine($"Celkem {photos.Count} fotografií. Přímé URL:");
            sb.AppendLine();
            foreach (var p in photos)
                sb.AppendLine($"- [{p.Name}]({p.DirectUrl})");
        }
        else
        {
            sb.AppendLine($"Viz složka **Fotky_z_inzeratu/** ({l.Photos.Count} fotografií ze scrapu).");
        }
        sb.AppendLine();
        sb.AppendLine("Vlastní fotky z prohlídky přidej do složky **Moje_fotky_z_prohlidky/**.");
        return sb.ToString();
    }

    public static string BuildDataJson(Listing l)
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

    /// <summary>
    /// Načte příslušný Markdown template ze složky Templates/ a interpoluje placeholdery.
    /// Templates lze měnit bez recompilace – stačí upravit soubor a restartovat aplikaci.
    /// </summary>
    public static string BuildAiInstructions(Listing l, IReadOnlyList<PhotoLink>? photos = null, string? folderUrl = null)
    {
        var price = l.Price.HasValue ? $"{l.Price.Value:N0} Kč" : "neuvedena";
        var area = l.AreaBuiltUp.HasValue
            ? $"{l.AreaBuiltUp} m² užitná" + (l.AreaLand.HasValue ? $" / {l.AreaLand} m² pozemek" : "")
            : (l.AreaLand.HasValue ? $"{l.AreaLand} m² pozemek" : "neuvedena");
        var isNewBuild = IsNewBuild(l.Condition, l.Description);

        var location = l.LocationText
            + (!string.IsNullOrWhiteSpace(l.Municipality) ? $", {l.Municipality}" : "")
            + (!string.IsNullOrWhiteSpace(l.District) ? $", okres {l.District}" : "")
            + (!string.IsNullOrWhiteSpace(l.Region) ? $", {l.Region}" : "");

        var templateName = isNewBuild ? "ai_instrukce_newbuild.md" : "ai_instrukce_existing.md";
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", templateName);
        var template = File.ReadAllText(templatePath);

        return template
            .Replace("{{LOCATION}}", location)
            .Replace("{{PROPERTY_TYPE}}", l.PropertyType.ToString())
            .Replace("{{OFFER_TYPE}}", l.OfferType.ToString())
            .Replace("{{PRICE}}", price)
            .Replace("{{PRICE_NOTE}}", !string.IsNullOrWhiteSpace(l.PriceNote) ? $" ({l.PriceNote})" : "")
            .Replace("{{AREA}}", area)
            .Replace("{{ROOMS_LINE}}", l.Rooms.HasValue ? $"**Počet pokojů:** {l.Rooms}\n" : "")
            .Replace("{{CONSTRUCTION_TYPE_LINE}}", !string.IsNullOrWhiteSpace(l.ConstructionType) ? $"**Typ konstrukce:** {l.ConstructionType}\n" : "")
            .Replace("{{CONDITION_LINE}}", !string.IsNullOrWhiteSpace(l.Condition) ? $"**Stav dle inzerátu:** {l.Condition}\n" : "")
            .Replace("{{SOURCE_NAME}}", l.SourceName ?? "")
            .Replace("{{SOURCE_CODE}}", l.SourceCode ?? "")
            .Replace("{{URL}}", l.Url ?? "")
            .Replace("{{PHOTO_LINKS_SECTION}}", BuildPhotoLinksInlineSection(photos))
            .Replace("{{DRIVE_FOLDER_SECTION}}", BuildDriveFolderSection(folderUrl));
    }

    private static string BuildPhotoLinksInlineSection(IReadOnlyList<PhotoLink>? photos)
    {
        if (photos is not { Count: > 0 }) return "";
        var sb = new StringBuilder();
        sb.AppendLine("### 📸 FOTKY Z INZERÁTU – přímé URL");
        sb.AppendLine();
        sb.AppendLine("> Přímé image URL – vlož je do zprávy pro AI nástroj (Perplexity, Claude, ChatGPT).");
        sb.AppendLine();
        foreach (var p in photos.Take(10))
        {
            var aiUrl = !string.IsNullOrEmpty(p.OriginalSourceUrl) ? p.OriginalSourceUrl : p.DirectUrl;
            sb.AppendLine($"- [{p.Name}]({aiUrl})");
        }
        if (photos.Count > 10)
            sb.AppendLine($"- ... a dalších {photos.Count - 10} fotek v souboru `FOTKY_LINKY.md`");
        sb.AppendLine();
        sb.AppendLine("**Blok pro AI chat (zkopíruj celý):**");
        sb.AppendLine("```");
        foreach (var p in photos.Take(10))
        {
            var aiUrl = !string.IsNullOrEmpty(p.OriginalSourceUrl) ? p.OriginalSourceUrl : p.DirectUrl;
            sb.AppendLine(aiUrl);
        }
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildDriveFolderSection(string? folderUrl)
    {
        if (string.IsNullOrWhiteSpace(folderUrl)) return "";
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 💾 INSTRUKCE: Uložení výsledku");
        sb.AppendLine();
        sb.AppendLine("Po dokončení analýzy:");
        sb.AppendLine("1. Zkopíruj celý text své analýzy");
        sb.AppendLine("2. V aplikaci Real Estate Aggregator na detail stránce tohoto inzerátu");
        sb.AppendLine("3. Vlož text do pole **\"Výsledek analýzy z AI\"** a klikni **Uložit analýzu**");
        sb.AppendLine($"4. Nebo ulož přímo do cloudové složky: [{folderUrl}]({folderUrl})");
        return sb.ToString();
    }

    public static bool IsNewBuild(string? condition, string? description)
    {
        var haystack = $"{condition} {description}".ToLowerInvariant();
        return haystack.Contains("novostavb") || haystack.Contains("ve výstavb") ||
               haystack.Contains("pod klíč") || haystack.Contains("developerský projekt") ||
               haystack.Contains("dokončení 202") ||
               condition?.ToLowerInvariant().Contains("nový") == true ||
               condition?.ToLowerInvariant().Contains("nová") == true;
    }

    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
            sb.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length > 100 ? result[..100] : result;
    }
}
