using Microsoft.Extensions.DependencyInjection;
using RealEstate.Infrastructure.BackgroundServices;
using RealEstate.Infrastructure.Scraping;

namespace RealEstate.Infrastructure;

/// <summary>
/// Extension methods for registering Playwright scraping services.
/// </summary>
public static class ScrapingServiceCollectionExtensions
{
    /// <summary>
    /// Registers Playwright scraper as a singleton service with browser initialization on startup.
    /// </summary>
    public static IServiceCollection AddPlaywrightScraping(this IServiceCollection services)
    {
        // Register scraper as singleton - single browser instance for the app
        services.AddSingleton<PlaywrightScraper>();

        // Register hosted service to initialize browser on startup
        services.AddHostedService<PlaywrightBootstrapHostedService>();

        return services;
    }
}
