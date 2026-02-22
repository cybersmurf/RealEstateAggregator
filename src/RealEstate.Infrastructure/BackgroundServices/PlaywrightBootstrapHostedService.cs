using Microsoft.Extensions.Hosting;
using RealEstate.Infrastructure.Scraping;

namespace RealEstate.Infrastructure.BackgroundServices;

/// <summary>
/// Hosted service that initializes Playwright browser on application startup.
/// This ensures the browser is ready before any scraping requests.
/// </summary>
public sealed class PlaywrightBootstrapHostedService : IHostedService
{
    private readonly PlaywrightScraper _scraper;

    public PlaywrightBootstrapHostedService(PlaywrightScraper scraper)
    {
        _scraper = scraper;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _scraper.InitializeAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
