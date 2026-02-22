using Microsoft.Playwright;
using Microsoft.Extensions.Logging;

namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Scraper pro získání seznamu inzerátů z list stránky REMAX.
/// Optimalizován s resource blocking pro rychlé načítání.
/// </summary>
public sealed class RemaxListScraper
{
    private readonly IBrowser _browser;
    private readonly ILogger<RemaxListScraper>? _logger;

    public RemaxListScraper(IBrowser browser, ILogger<RemaxListScraper>? logger = null)
    {
        _browser = browser;
        _logger = logger;
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

        // Extended selector fallback chain - pokus se najít liste element s různými CSS třídami
        // Nejdřív zkusí REMAX-specificke selektory, pak generičtější selektory
        var selectors = new[]
        {
            // REMAX-specificke
            ".remax-search-result-item",
            ".remax-item",
            "div[class*='remax'][class*='item']",
            "div[class*='property'][class*='item']",
            "div[class*='realty'][class*='item']",
            
            // Generické
            ".property-item",
            ".property-card",
            ".listing-item",
            ".realty-item",
            ".search-result",
            ".product-item",
            ".real-estate-item",
            "article[class*='property']",
            "article[class*='item']",
            "div[data-property-id]",
            "div[data-listing-id]",
            
            // Poslední pokus - všechny divy v .search-results
            ".search-results > div"
        };

        var cards = new List<IElementHandle>();
        foreach (var selector in selectors)
        {
            var found = await page.QuerySelectorAllAsync(selector);
            if (found.Count > 0)
            {
                _logger?.LogInformation($"RemaxListScraper: Found {found.Count} items with selector '{selector}'");
                cards = found.ToList();
                break;
            }
        }

        if (cards.Count == 0)
        {
            _logger?.LogWarning("RemaxListScraper: No property cards found with any selector. Page HTML might have changed.");
        }

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
