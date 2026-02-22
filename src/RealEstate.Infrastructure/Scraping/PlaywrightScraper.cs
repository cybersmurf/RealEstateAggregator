using Microsoft.Playwright;

namespace RealEstate.Infrastructure.Scraping;

/// <summary>
/// Playwright-based web scraper service for JavaScript-heavy websites.
/// Optimized for performance with resource blocking and context reuse.
/// </summary>
public sealed class PlaywrightScraper : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    /// <summary>
    /// Initializes the Playwright browser instance.
    /// Should be called once on startup (via IHostedService).
    /// </summary>
    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-gpu",
                "--disable-notifications",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding"
            }
        });
    }

    /// <summary>
    /// Scrapes a single page and returns the HTML content.
    /// </summary>
    /// <param name="url">URL to scrape</param>
    /// <param name="contentSelector">CSS selector to wait for (ensures content is loaded)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>HTML content of the page</returns>
    public async Task<string> ScrapeHtmlAsync(
        string url,
        string contentSelector,
        CancellationToken ct = default)
    {
        if (_browser is null)
            throw new InvalidOperationException("Browser is not initialized. Call InitializeAsync() first.");

        var context = await _browser.NewContextAsync();
        await OptimizeContextAsync(context);

        var page = await context.NewPageAsync();

        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30000
        });

        await page.WaitForSelectorAsync(contentSelector, new PageWaitForSelectorOptions
        {
            Timeout = 10000
        });

        var html = await page.ContentAsync();
        await context.CloseAsync();

        return html;
    }

    /// <summary>
    /// Scrapes multiple pages in parallel with controlled concurrency.
    /// </summary>
    /// <param name="urls">List of URLs to scrape</param>
    /// <param name="contentSelector">CSS selector to wait for on each page</param>
    /// <param name="maxDegreeOfParallelism">Maximum number of concurrent requests (default: 8)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of HTML content for each URL</returns>
    public async Task<IReadOnlyList<string>> ScrapeManyAsync(
        IEnumerable<string> urls,
        string contentSelector,
        int maxDegreeOfParallelism = 8,
        CancellationToken ct = default)
    {
        if (_browser is null)
            throw new InvalidOperationException("Browser is not initialized. Call InitializeAsync() first.");

        var throttler = new SemaphoreSlim(maxDegreeOfParallelism);
        var tasks = urls.Select(async url =>
        {
            await throttler.WaitAsync(ct);
            try
            {
                var context = await _browser.NewContextAsync();
                await OptimizeContextAsync(context);

                var page = await context.NewPageAsync();
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });

                await page.WaitForSelectorAsync(contentSelector, new PageWaitForSelectorOptions
                {
                    Timeout = 10000
                });

                var html = await page.ContentAsync();
                await context.CloseAsync();

                return html;
            }
            finally
            {
                throttler.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Optimizes browser context by blocking unnecessary resources (images, fonts, stylesheets, media).
    /// This significantly speeds up page loading.
    /// </summary>
    private static async Task OptimizeContextAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;

            // Block heavy resources that are not needed for data extraction
            if (request.ResourceType is "image" or "stylesheet" or "font" or "media")
            {
                await route.AbortAsync();
                return;
            }

            await route.ContinueAsync();
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
    }
}
