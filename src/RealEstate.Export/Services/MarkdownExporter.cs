using System.Text;
using System.Text.Json;
using RealEstate.Domain.Entities;

namespace RealEstate.Export.Services;

/// <summary>
/// Exportuje inzeráty do Markdown formátu (optimální pro AI analýzu)
/// </summary>
public class MarkdownExporter : IExportService
{
    public async Task<string> ExportListingAsync(Listing listing, ExportFormat format, CancellationToken ct = default)
    {
        return format switch
        {
            ExportFormat.Markdown => BuildMarkdown(listing),
            ExportFormat.Json => BuildJson(listing),
            ExportFormat.Html => BuildHtml(listing),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    public async Task<string> ExportListingsAsync(IEnumerable<Listing> listings, ExportFormat format, CancellationToken ct = default)
    {
        return format switch
        {
            ExportFormat.Markdown => BuildMarkdownBatch(listings),
            ExportFormat.Json => BuildJsonBatch(listings),
            ExportFormat.Html => BuildHtmlBatch(listings),
            _ => throw new ArgumentException($"Unsupported format: {format}")
        };
    }

    // ============================================================================
    // MARKDOWN (primary format for AI analysis)
    // ============================================================================

    private string BuildMarkdown(Listing listing)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {listing.Title}");
        sb.AppendLine();

        // Metadata sekce
        sb.AppendLine("## 📋 Metadata");
        sb.AppendLine();
        sb.AppendLine("| Parametr | Hodnota |");
        sb.AppendLine("|----------|---------|");
        sb.AppendLine($"| **ID** | `{listing.Id}` |");
        sb.AppendLine($"| **Zdroj** | {listing.SourceName} ({listing.SourceCode}) |");
        sb.AppendLine($"| **URL** | [{listing.Url}]({listing.Url}) |");
        sb.AppendLine($"| **Lokalita** | {listing.LocationText} |");
        sb.AppendLine($"| **Región** | {listing.Region} |");
        sb.AppendLine($"| **Okres** | {listing.District} |");
        sb.AppendLine($"| **Obec** | {listing.Municipality} |");
        sb.AppendLine();

        // Vlastnosti nemovitosti
        sb.AppendLine("## 🏠 Vlastnosti nemovitosti");
        sb.AppendLine();
        sb.AppendLine("| Vlastnost | Hodnota |");
        sb.AppendLine("|-----------|---------|");
        sb.AppendLine($"| **Typ** | {listing.PropertyType} |");
        sb.AppendLine($"| **Nabídka** | {listing.OfferType} |");
        sb.AppendLine($"| **Cena** | {FormatPrice(listing.Price)} {(listing.PriceNote != null ? $"({listing.PriceNote})" : "")} |");
        sb.AppendLine($"| **Plocha stavby** | {FormatArea(listing.AreaBuiltUp)} |");
        sb.AppendLine($"| **Plocha pozemku** | {FormatArea(listing.AreaLand)} |");
        sb.AppendLine($"| **Počet místností** | {listing.Rooms ?? 0} |");
        sb.AppendLine($"| **Kuchyň** | {(listing.HasKitchen == true ? "Ano" : listing.HasKitchen == false ? "Ne" : "Neuvedeno")} |");
        sb.AppendLine($"| **Typ stavby** | {listing.ConstructionType ?? "Neuvedeno"} |");
        sb.AppendLine($"| **Stav** | {listing.Condition ?? "Neuvedeno"} |");
        sb.AppendLine();

        // Popis
        sb.AppendLine("## 📝 Popis");
        sb.AppendLine();
        sb.AppendLine(listing.Description);
        sb.AppendLine();

        // Fotky
        if (listing.Photos?.Count > 0)
        {
            sb.AppendLine("## 📸 Fotky");
            sb.AppendLine();
            foreach (var (photo, index) in listing.Photos.Select((p, i) => (p, i)))
            {
                sb.AppendLine($"### Fotka {index + 1}");
                sb.AppendLine();
                sb.AppendLine($"![Foto]({photo.OriginalUrl})");
                sb.AppendLine();
            }
        }

        // Timeline
        sb.AppendLine("## 📅 Timeline");
        sb.AppendLine();
        sb.AppendLine("| Datum | Akce |");
        sb.AppendLine("|-------|------|");
        sb.AppendLine($"| {listing.FirstSeenAt:yyyy-MM-dd HH:mm} | Poprvé viděno |");
        if (listing.LastSeenAt.HasValue)
            sb.AppendLine($"| {listing.LastSeenAt:yyyy-MM-dd HH:mm} | Naposledy viděno |");
        if (listing.CreatedAtSource.HasValue)
            sb.AppendLine($"| {listing.CreatedAtSource:yyyy-MM-dd HH:mm} | Vytvořeno na zdroji |");
        if (listing.UpdatedAtSource.HasValue)
            sb.AppendLine($"| {listing.UpdatedAtSource:yyyy-MM-dd HH:mm} | Aktualizováno na zdroji |");
        sb.AppendLine($"| {DateTime.UtcNow:yyyy-MM-dd HH:mm} | Export vytvořen |");
        sb.AppendLine();

        // Status
        sb.AppendLine("## ✓ Status");
        sb.AppendLine();
        sb.AppendLine($"- **Aktivní**: {(listing.IsActive ? "✅ Ano" : "❌ Ne")}");
        sb.AppendLine($"- **Embeddingy generovány**: {(listing.DescriptionEmbedding != null ? "✅ Ano" : "❌ Ne")}");
        sb.AppendLine();

        return sb.ToString();
    }

    private string BuildMarkdownBatch(IEnumerable<Listing> listings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Balíček inzerátů pro analýzu");
        sb.AppendLine();
        sb.AppendLine($"**Generováno**: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        sb.AppendLine("## Obsah");
        sb.AppendLine();

        var listingList = listings.ToList();
        for (int i = 0; i < listingList.Count; i++)
        {
            var listing = listingList[i];
            sb.AppendLine($"{i + 1}. [{listing.Title}](#{listing.Id})");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var listing in listingList)
        {
            sb.Append(BuildMarkdown(listing));
            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ============================================================================
    // JSON
    // ============================================================================

    private string BuildJson(Listing listing)
    {
        var dto = new
        {
            listing.Id,
            listing.SourceCode,
            listing.SourceName,
            listing.Title,
            listing.Description,
            listing.Url,
            listing.LocationText,
            listing.Region,
            listing.District,
            listing.Municipality,
            listing.PropertyType,
            listing.OfferType,
            listing.Price,
            listing.PriceNote,
            listing.AreaBuiltUp,
            listing.AreaLand,
            listing.Rooms,
            listing.HasKitchen,
            listing.ConstructionType,
            listing.Condition,
            listing.FirstSeenAt,
            listing.LastSeenAt,
            listing.CreatedAtSource,
            listing.UpdatedAtSource,
            listing.IsActive,
            PhotoCount = listing.Photos?.Count ?? 0,
            Photos = listing.Photos?.Select(p => new { p.OriginalUrl, p.StoredUrl, p.Order })
        };

        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildJsonBatch(IEnumerable<Listing> listings)
    {
        var dtos = listings.Select(l => new
        {
            l.Id,
            l.SourceCode,
            l.SourceName,
            l.Title,
            l.Url,
            l.LocationText,
            l.Price,
            l.AreaBuiltUp,
            l.AreaLand,
            l.PropertyType,
            l.IsActive,
            l.FirstSeenAt
        });

        return JsonSerializer.Serialize(new { Count = dtos.Count(), Listings = dtos }, 
            new JsonSerializerOptions { WriteIndented = true });
    }

    // ============================================================================
    // HTML (zdarma preview v prohlížeči)
    // ============================================================================

    private string BuildHtml(Listing listing)
    {
        var markdown = BuildMarkdown(listing);
        // Zjednodušený HTML - v reálu bys chtěl Markdig nebo Commonmark
        var html = markdown
            .Replace("\n## ", "\n<h2>")
            .Replace("| ", "<tr><td>")
            .Replace("|", "</td><td>");

        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>{listing.Title}</title>
    <style>
        body {{ font-family: Arial, sans-serif; max-width: 900px; margin: 0 auto; padding: 20px; }}
        h1, h2 {{ color: #333; }}
        table {{ border-collapse: collapse; width: 100%; }}
        td, th {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
        img {{ max-width: 100%; margin: 10px 0; }}
    </style>
</head>
<body>
    <pre>{HtmlEscape(markdown)}</pre>
</body>
</html>";
    }

    private string BuildHtmlBatch(IEnumerable<Listing> listings)
    {
        var htmls = listings.Select(l => BuildHtml(l));
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <title>Balíček inzerátů</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; }}
        .listing {{ page-break-inside: avoid; border: 1px solid #ccc; padding: 20px; margin: 20px 0; }}
    </style>
</head>
<body>
    {string.Join("\n<hr>\n", htmls.Select(h => $"<div class='listing'>{h}</div>"))}
</body>
</html>";
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static string FormatPrice(decimal? price)
    {
        if (!price.HasValue) return "Neuvedeno";
        return price >= 1_000_000 
            ? $"{price / 1_000_000:F1}M Kč"
            : $"{price / 1000:F0}k Kč";
    }

    private static string FormatArea(double? area)
    {
        return area.HasValue ? $"{area:F0} m²" : "Neuvedeno";
    }

    private static string HtmlEscape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
