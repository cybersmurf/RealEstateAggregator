using System.Globalization;
using System.Text;

namespace RealEstate.Api.Services.Notifications;

/// <summary>Položka digestu – nový inzerát nebo zlevnění.</summary>
public sealed record NotificationListingItem(Guid ListingId, string Title, decimal? Price, string Location);

public sealed record NotificationPriceDropItem(Guid ListingId, string Title, decimal OldPrice, decimal NewPrice, string Location);

/// <summary>
/// Sestavení digestu upozornění pro e-mail (plné HTML + text) a Telegram (omezené HTML).
/// Statická a bez závislostí, aby šla testovat. Odkazy jsou absolutní přes APP_PUBLIC_URL.
/// </summary>
public static class NotificationMessageBuilder
{
    private static readonly CultureInfo Cs = CultureInfo.GetCultureInfo("cs-CZ");

    /// <summary>Telegram limituje zprávu na 4096 znaků – po tomto počtu položek se zbytek shrne.</summary>
    private const int TelegramMaxItemsPerSection = 15;

    public static string ListingUrl(string appBaseUrl, Guid listingId) =>
        $"{NormalizeBase(appBaseUrl)}/listings/{listingId}";

    public static string SavedSearchesUrl(string appBaseUrl) =>
        $"{NormalizeBase(appBaseUrl)}/saved-searches";

    public static string BuildSubject(string searchName, int newCount, int dropCount)
    {
        var parts = new List<string>();
        if (newCount > 0) parts.Add(newCount == 1 ? "1 nový inzerát" : $"{newCount} nových inzerátů");
        if (dropCount > 0) parts.Add(dropCount == 1 ? "1 zlevnění" : $"{dropCount} zlevnění");
        var summary = parts.Count == 0 ? "bez změn" : string.Join(", ", parts);
        return $"{searchName}: {summary}";
    }

    /// <summary>Procentuální pokles ceny, zaokrouhlený na celé procento (kladné číslo).</summary>
    public static int DropPercent(decimal oldPrice, decimal newPrice)
    {
        if (oldPrice <= 0) return 0;
        return (int)Math.Round((oldPrice - newPrice) / oldPrice * 100, MidpointRounding.AwayFromZero);
    }

    public static string FormatPrice(decimal? price) =>
        price is null ? "cena neuvedena" : string.Format(Cs, "{0:N0} Kč", price.Value).Replace(' ', ' ');

    public static string BuildEmailHtml(
        string searchName,
        IReadOnlyList<NotificationListingItem> newListings,
        IReadOnlyList<NotificationPriceDropItem> priceDrops,
        string appBaseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"cs\"><body style=\"font-family:Arial,Helvetica,sans-serif;color:#222;max-width:640px;margin:0 auto;padding:16px\">");
        sb.Append("<h2 style=\"margin:0 0 4px 0\">").Append(E(searchName)).Append("</h2>");
        sb.Append("<p style=\"margin:0 0 16px 0;color:#666\">Uložené hledání – přehled změn</p>");

        if (newListings.Count > 0)
        {
            sb.Append("<h3 style=\"margin:16px 0 8px 0\">Nové inzeráty (").Append(newListings.Count).Append(")</h3><ul style=\"padding-left:18px\">");
            foreach (var item in newListings)
            {
                sb.Append("<li style=\"margin-bottom:8px\"><a href=\"").Append(ListingUrl(appBaseUrl, item.ListingId)).Append("\" style=\"color:#4A6FA5;font-weight:bold\">")
                  .Append(E(item.Title)).Append("</a><br/><span style=\"color:#444\">")
                  .Append(E(FormatPrice(item.Price)));
                if (!string.IsNullOrWhiteSpace(item.Location))
                    sb.Append(" · ").Append(E(item.Location));
                sb.Append("</span></li>");
            }
            sb.Append("</ul>");
        }

        if (priceDrops.Count > 0)
        {
            sb.Append("<h3 style=\"margin:16px 0 8px 0\">Zlevnění (").Append(priceDrops.Count).Append(")</h3><ul style=\"padding-left:18px\">");
            foreach (var item in priceDrops)
            {
                sb.Append("<li style=\"margin-bottom:8px\"><a href=\"").Append(ListingUrl(appBaseUrl, item.ListingId)).Append("\" style=\"color:#4A6FA5;font-weight:bold\">")
                  .Append(E(item.Title)).Append("</a><br/><span style=\"color:#444\">")
                  .Append(E(FormatPrice(item.OldPrice))).Append(" → <strong>").Append(E(FormatPrice(item.NewPrice))).Append("</strong>")
                  .Append(" <span style=\"color:#2e7d32\">−").Append(DropPercent(item.OldPrice, item.NewPrice)).Append(" %</span>");
                if (!string.IsNullOrWhiteSpace(item.Location))
                    sb.Append(" · ").Append(E(item.Location));
                sb.Append("</span></li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<hr style=\"border:0;border-top:1px solid #ddd;margin:24px 0 12px 0\"/>");
        sb.Append("<p style=\"font-size:12px;color:#888\">Nastavení upozornění: <a href=\"").Append(SavedSearchesUrl(appBaseUrl))
          .Append("\" style=\"color:#4A6FA5\">Uložená hledání</a></p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    public static string BuildEmailText(
        string searchName,
        IReadOnlyList<NotificationListingItem> newListings,
        IReadOnlyList<NotificationPriceDropItem> priceDrops,
        string appBaseUrl)
    {
        var sb = new StringBuilder();
        sb.Append(searchName).Append(" – přehled změn\n\n");

        if (newListings.Count > 0)
        {
            sb.Append("Nové inzeráty (").Append(newListings.Count).Append("):\n");
            foreach (var item in newListings)
            {
                sb.Append("- ").Append(item.Title).Append(" | ").Append(FormatPrice(item.Price));
                if (!string.IsNullOrWhiteSpace(item.Location)) sb.Append(" | ").Append(item.Location);
                sb.Append('\n').Append("  ").Append(ListingUrl(appBaseUrl, item.ListingId)).Append('\n');
            }
            sb.Append('\n');
        }

        if (priceDrops.Count > 0)
        {
            sb.Append("Zlevnění (").Append(priceDrops.Count).Append("):\n");
            foreach (var item in priceDrops)
            {
                sb.Append("- ").Append(item.Title).Append(" | ")
                  .Append(FormatPrice(item.OldPrice)).Append(" → ").Append(FormatPrice(item.NewPrice))
                  .Append(" (−").Append(DropPercent(item.OldPrice, item.NewPrice)).Append(" %)");
                if (!string.IsNullOrWhiteSpace(item.Location)) sb.Append(" | ").Append(item.Location);
                sb.Append('\n').Append("  ").Append(ListingUrl(appBaseUrl, item.ListingId)).Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("Nastavení upozornění: ").Append(SavedSearchesUrl(appBaseUrl)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Telegram HTML: jen b/i/a; bez seznamů. Delší digest se zkrátí, aby se vešel do 4096 znaků.</summary>
    public static string BuildTelegramHtml(
        string searchName,
        IReadOnlyList<NotificationListingItem> newListings,
        IReadOnlyList<NotificationPriceDropItem> priceDrops,
        string appBaseUrl)
    {
        var sb = new StringBuilder();
        sb.Append("<b>").Append(E(searchName)).Append("</b>\n");

        if (newListings.Count > 0)
        {
            sb.Append("\n<b>Nové inzeráty (").Append(newListings.Count).Append(")</b>\n");
            foreach (var item in newListings.Take(TelegramMaxItemsPerSection))
            {
                sb.Append("• <a href=\"").Append(ListingUrl(appBaseUrl, item.ListingId)).Append("\">").Append(E(item.Title)).Append("</a> – ")
                  .Append(E(FormatPrice(item.Price)));
                if (!string.IsNullOrWhiteSpace(item.Location)) sb.Append(" · ").Append(E(item.Location));
                sb.Append('\n');
            }
            if (newListings.Count > TelegramMaxItemsPerSection)
                sb.Append("… a dalších ").Append(newListings.Count - TelegramMaxItemsPerSection).Append('\n');
        }

        if (priceDrops.Count > 0)
        {
            sb.Append("\n<b>Zlevnění (").Append(priceDrops.Count).Append(")</b>\n");
            foreach (var item in priceDrops.Take(TelegramMaxItemsPerSection))
            {
                sb.Append("• <a href=\"").Append(ListingUrl(appBaseUrl, item.ListingId)).Append("\">").Append(E(item.Title)).Append("</a> – ")
                  .Append(E(FormatPrice(item.OldPrice))).Append(" → <b>").Append(E(FormatPrice(item.NewPrice))).Append("</b> (−")
                  .Append(DropPercent(item.OldPrice, item.NewPrice)).Append(" %)");
                if (!string.IsNullOrWhiteSpace(item.Location)) sb.Append(" · ").Append(E(item.Location));
                sb.Append('\n');
            }
            if (priceDrops.Count > TelegramMaxItemsPerSection)
                sb.Append("… a dalších ").Append(priceDrops.Count - TelegramMaxItemsPerSection).Append('\n');
        }

        sb.Append("\n<a href=\"").Append(SavedSearchesUrl(appBaseUrl)).Append("\">Nastavení upozornění</a>");
        return sb.ToString();
    }

    public static string BuildTestTelegramHtml(string searchName, string appBaseUrl) =>
        $"<b>Zkušební zpráva</b>\nUpozornění pro uložené hledání <b>{E(searchName)}</b> budou chodit sem.\n\n" +
        $"<a href=\"{SavedSearchesUrl(appBaseUrl)}\">Uložená hledání</a>";

    public static string BuildTestEmailHtml(string searchName, string appBaseUrl) =>
        "<!DOCTYPE html><html lang=\"cs\"><body style=\"font-family:Arial,Helvetica,sans-serif;color:#222;max-width:640px;margin:0 auto;padding:16px\">" +
        $"<h2>Zkušební zpráva</h2><p>Upozornění pro uložené hledání <strong>{E(searchName)}</strong> budou chodit na tento e-mail.</p>" +
        $"<p style=\"font-size:12px;color:#888\"><a href=\"{SavedSearchesUrl(appBaseUrl)}\" style=\"color:#4A6FA5\">Uložená hledání</a></p></body></html>";

    public static string BuildTestEmailText(string searchName, string appBaseUrl) =>
        $"Zkušební zpráva\n\nUpozornění pro uložené hledání „{searchName}“ budou chodit na tento e-mail.\n\n{SavedSearchesUrl(appBaseUrl)}\n";

    private static string NormalizeBase(string appBaseUrl) =>
        string.IsNullOrWhiteSpace(appBaseUrl) ? "http://localhost:5002" : appBaseUrl.TrimEnd('/');

    /// <summary>Escapuje jen &amp; &lt; &gt; " – WebUtility.HtmlEncode by českou diakritiku převedl na číselné entity (Telegram to nezobrazí).</summary>
    private static string E(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
