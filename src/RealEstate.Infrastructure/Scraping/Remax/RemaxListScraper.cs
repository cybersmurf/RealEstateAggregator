using Microsoft.Playwright;

namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Scraper pro získání seznamu inzerátů z list stránky REMAX.
/// Optimalizován s resource blocking pro rychlé načítání.
/// </summary>
public sealed class RemaxListScraper
{
    private readonly IBrowser _browser;

    public RemaxListScraper(IBrowser browser)
    {
        _browser = browser;
    }

    public async Task<IReadOnlyList<RemaxListItem>> ScrapeListAsync(
        string listUrl,
        CancellationToken ct = default)
    {
        var context = await _browser.NewContextAsync();
        await OptimizeContextAsync(context);

        var page = await context.NewPageAsync();

        await page.GotoAsync(listUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });

        // Čekání na wrapper listu - selector přizpůsoben podle HTML Remaxu
        try
        {
            await page.WaitForSelectorAsync(".search-results, .realty-list, .properties-list", new PageWaitForSelectorOptions
            {
                Timeout = 15000
            });
        }
        catch
        {
            // Pokud selector neexistuje, pokračujeme - může být prázdný seznam
        }

        var items = new List<RemaxListItem>();

        // Přizpůsob podle skutečných tříd Remaxu
        // Možné selektory: .remax-search-result-item, .property-item, .realty-item
        var cards = await page.QuerySelectorAllAsync(
            ".remax-search-result-item, .property-item, .realty-item, .search-result");

        foreach (var card in cards)
        {
            try
            {
                var titleEl = await card.QuerySelectorAsync(
                    ".remax-search-result-title a, .property-title a, h2 a, h3 a");
                var locEl = await card.QuerySelectorAsync(
                    ".remax-search-result-location, .property-location, .location");
                var priceEl = await card.QuerySelectorAsync(
                    ".remax-search-result-price, .property-price, .price");

                var title = (await titleEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
                var href = (await titleEl?.GetAttributeAsync("href")!) ?? string.Empty;
                var location = (await locEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
                var priceText = (await priceEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(href))
                    continue;

                var absoluteUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? href
                    : $"https://www.remax-czech.cz{href}";

                var item = new RemaxListItem
                {
                    Title = title,
                    DetailUrl = absoluteUrl,
                    LocationText = location,
                    Price = ParsePrice(priceText)
                };

                items.Add(item);
            }
            catch
            {
                // Přeskočit chybné karty
                continue;
            }
        }

        await context.CloseAsync();
        return items;
    }

    private static decimal? ParsePrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // např. "6 990 000 Kč" → 6990000
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (decimal.TryParse(digits, out var value))
            return value;

        return null;
    }

    private static async Task OptimizeContextAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var req = route.Request;
            if (req.ResourceType is "image" or "stylesheet" or "font" or "media")
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });
    }
}
