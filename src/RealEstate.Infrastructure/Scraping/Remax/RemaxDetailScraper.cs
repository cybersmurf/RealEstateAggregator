using Microsoft.Playwright;

namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Scraper pro získání detailů inzerátu REMAX.
/// Optimalizován s resource blocking pro rychlé načítání.
/// </summary>
public sealed class RemaxDetailScraper
{
    private readonly IBrowser _browser;

    public RemaxDetailScraper(IBrowser browser)
    {
        _browser = browser;
    }

    public async Task<RemaxDetailResult> ScrapeDetailAsync(
        RemaxListItem listItem,
        CancellationToken ct = default)
    {
        var context = await _browser.NewContextAsync();
        await OptimizeContextAsync(context);

        var page = await context.NewPageAsync();

        await page.GotoAsync(listItem.DetailUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });

        // Čekání na hlavní detail container – přizpůsob selektor
        try
        {
            await page.WaitForSelectorAsync(".property-detail, .realty-detail, .detail-container", new PageWaitForSelectorOptions
            {
                Timeout = 15000
            });
        }
        catch
        {
            // Pokračujeme i bez selektoru
        }

        // Titulek
        var titleEl = await page.QuerySelectorAsync("h1, .property-title, .detail-title");
        var title = (await titleEl?.InnerTextAsync()!)?.Trim() ?? listItem.Title;

        // Popis
        var descEl = await page.QuerySelectorAsync(
            ".property-detail__description, .remax-property-description, .detail-description, .description");
        var description = (await descEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;

        // Cena a poznámka
        var priceEl = await page.QuerySelectorAsync(
            ".property-detail__price-main, .property-price, .price-main, .price");
        var priceNoteEl = await page.QuerySelectorAsync(
            ".property-detail__price-note, .price-note");

        var priceText = (await priceEl?.InnerTextAsync()!)?.Trim() ?? string.Empty;
        var price = listItem.Price ?? ParsePrice(priceText);
        var priceNote = (await priceNoteEl?.InnerTextAsync()!)?.Trim();

        // Parametry – často bývají v tabulce/ul-li
        var paramRows = await page.QuerySelectorAllAsync(
            ".property-detail__parameters tr, .parameters tr, .detail-params tr");
        
        double? areaBuiltUp = null;
        double? areaLand = null;

        foreach (var row in paramRows)
        {
            var cells = await row.QuerySelectorAllAsync("td, th");
            if (cells.Count < 2) continue;

            var name = (await cells[0].InnerTextAsync())?.Trim().ToLowerInvariant();
            var value = (await cells[1].InnerTextAsync())?.Trim() ?? string.Empty;

            if (name is null) continue;

            if (name.Contains("užitná plocha") || name.Contains("podlahová plocha") || name.Contains("plocha domu"))
            {
                areaBuiltUp = ParseArea(value);
            }
            else if (name.Contains("plocha pozemku") || name.Contains("celková plocha"))
            {
                areaLand = ParseArea(value);
            }
        }

        // Alternativně zkusit ul/li strukturu
        if (areaBuiltUp is null || areaLand is null)
        {
            var paramItems = await page.QuerySelectorAllAsync(
                ".property-detail__parameters li, .parameters li, .params-list li");

            foreach (var item in paramItems)
            {
                var text = (await item.InnerTextAsync())?.Trim().ToLowerInvariant() ?? string.Empty;

                if ((areaBuiltUp is null) && (text.Contains("užitná plocha") || text.Contains("podlahová plocha")))
                {
                    areaBuiltUp = ParseArea(text);
                }
                else if ((areaLand is null) && (text.Contains("plocha pozemku") || text.Contains("celková plocha")))
                {
                    areaLand = ParseArea(text);
                }
            }
        }

        // Fotky – typicky carousel nebo gallery
        var photoEls = await page.QuerySelectorAllAsync(
            ".property-gallery img, .property-detail__gallery img, .gallery img, .photos img");

        var photos = new List<string>();
        foreach (var img in photoEls)
        {
            var src = await img.GetAttributeAsync("src");
            var dataSrc = await img.GetAttributeAsync("data-src");
            
            // Preferuj data-src (lazy loading)
            var photoUrl = !string.IsNullOrWhiteSpace(dataSrc) ? dataSrc : src;
            
            if (string.IsNullOrWhiteSpace(photoUrl))
                continue;

            var abs = photoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? photoUrl
                : $"https://www.remax-czech.cz{photoUrl}";

            // Ignoruj thumbnaily a placeholdery
            if (abs.Contains("thumb") || abs.Contains("placeholder"))
                continue;

            photos.Add(abs);
        }

        await context.CloseAsync();

        return new RemaxDetailResult
        {
            Title = title,
            Url = listItem.DetailUrl,
            LocationText = listItem.LocationText,
            Description = description,
            Price = price,
            PriceNote = priceNote,
            AreaBuiltUp = areaBuiltUp,
            AreaLand = areaLand,
            PropertyType = "House",
            PhotoUrls = photos.Distinct().ToList()
        };
    }

    private static decimal? ParsePrice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (decimal.TryParse(digits, out var value))
            return value;
        
        return null;
    }

    private static double? ParseArea(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // např. "182 m²" nebo "Užitná plocha: 182 m²"
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (double.TryParse(digits, out var value))
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
