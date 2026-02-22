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
        // Nejdřív zkusí REMAX-specificke selektory (aktuální HTML struktura), pak staré, pak generičtější selektory
        var selectors = new[]
        {
            // REMAX CURRENT (2024-2025): pl-items__item
            ".pl-items__item",
            "div[class*='pl-items'][class*='__item']",
            
            // REMAX CURRENT (2024-2025): catalog-hp__list-item
            ".catalog-hp__list-item",
            "div[class*='catalog'][class*='list']",
            
            // REMAX-specific (older versions)
            ".remax-search-result-item",
            ".remax-item",
            "div[class*='remax'][class*='item']",
            
            // Generic property listing selectors
            "div[class*='property'][class*='item']",
            ".property-item",
            ".property-card",
            ".listing-item",
            
            // Realty-specific
            ".realty-item",
            ".search-result",
            "div[class*='realty'][class*='item']",
            
            // Generic fallbacks
            ".product-item",
            ".real-estate-item",
            "article[class*='property']",
            "article[class*='item']",
            "div[data-property-id]",
            "div[data-listing-id]",
            
            // Last resort - find all divs with links to details
            "div[class*='item'] a[href*='nemovitost']"
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
            
            // Debug: Get page structure
            var pageTitle = await page.TitleAsync();
            var bodyText = await page.InnerTextAsync("body");
            var allDivs = await page.QuerySelectorAllAsync("div[class*='property'], div[class*='item'], article");
            
            _logger?.LogWarning("Page title: {Title}", pageTitle);
            _logger?.LogWarning("Found {DivCount} divs with property/item/article patterns", allDivs.Count);
            
            if (bodyText.Length > 500)
                _logger?.LogWarning("Page text (first 500 chars): {Text}...", bodyText[..500]);
            else
                _logger?.LogWarning("Full page text: {Text}", bodyText);
        }

        foreach (var card in cards)
        {
            try
            {
                // Extract from data attributes (most reliable)
                var dataTitle = await card.GetAttributeAsync("data-title");
                var dataPrice = await card.GetAttributeAsync("data-price");
                
                // Find detail link
                var linkEl = await card.QuerySelectorAsync("a[href*='nemovitost'], a[href*='detail'], h2 a, .pl-items__link");
                var href = (await linkEl?.GetAttributeAsync("href")!) ?? string.Empty;

                // Fallback: Extract from visible text if data attributes missing
                var title = dataTitle;
                if (string.IsNullOrWhiteSpace(title))
                {
                    var titleEl = await card.QuerySelectorAsync("h2, .pl-items__item-info h2, [class*='title']");
                    title = (await titleEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
                }

                // Extract location
                var locEl = await card.QuerySelectorAsync(".pl-items__item-info p, .property-location, .location");
                var location = (await locEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
                
                // Extract price
                var priceText = dataPrice;
                if (string.IsNullOrWhiteSpace(priceText))
                {
                    var priceEl = await card.QuerySelectorAsync(".pl-items__item-price, .property-price, .price");
                    priceText = (await priceEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(href) && !string.IsNullOrWhiteSpace(dataTitle))
                {
                    _logger?.LogDebug("RemaxListScraper: Card has data-title but no link found: {Title}", dataTitle);
                    continue;
                }

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
                _logger?.LogDebug("RemaxListScraper: Added item: {Title} ({Url})", item.Title, item.DetailUrl);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "RemaxListScraper: Error parsing card");
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
