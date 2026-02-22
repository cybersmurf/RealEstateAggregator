using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RealEstate.Domain.Repositories;
using RealEstate.Infrastructure.Scraping.Remax;

namespace RealEstate.Api.Services;

/// <summary>
/// Generický service pro REMAX scraping s konfigurovatelem URL.
/// Umožňuje scrapovat libovolné REMAX URLs (Znojmo, Praha, atd.)
/// </summary>
public interface IRemaxScrapingService
{
    Task RunAsync(string listUrl, CancellationToken ct = default);
}

public sealed class RemaxScrapingService : IRemaxScrapingService
{
    private readonly ILogger<RemaxScrapingService> _logger;
    private readonly IListingRepository _listingRepository;
    private readonly ISourceService _sourceService;
    private readonly ILoggerFactory _loggerFactory;

    public RemaxScrapingService(
        ILogger<RemaxScrapingService> logger,
        IListingRepository listingRepository,
        ISourceService sourceService,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _listingRepository = listingRepository;
        _sourceService = sourceService;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Spustí scraping ze zadaného REMAX URL.
    /// 
    /// Příklady:
    /// - https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/
    /// - https://www.remax-czech.cz/reality/domy-a-vily/pronajeti/hlavni-mesto-praha/
    /// - https://www.remax-czech.cz/reality/byty/prodej/jihomoravsky-kraj/
    /// </summary>
    public async Task RunAsync(string listUrl, CancellationToken ct = default)
    {
        _logger.LogInformation("Spouštím REMAX scraping z URL: {Url}", listUrl);

        // Získej Source ID pro REMAX z databáze
        var source = await _sourceService.GetSourceByCodeAsync("REMAX", ct);
        if (source == null)
        {
            _logger.LogError("REMAX source not found in database");
            throw new InvalidOperationException("REMAX source not found");
        }

        // Vytvoříme vlastní Playwright instanci pouze pro tento import
        var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-gpu"
            }
        });

        var importerLogger = _loggerFactory.CreateLogger<RemaxZnojmoImporter>();
        var importer = new RemaxZnojmoImporter(browser, _listingRepository, importerLogger);
        await importer.ImportAsync(source.Id, ct);

        playwright.Dispose();
        _logger.LogInformation("REMAX scraping completion for URL: {Url}", listUrl);
    }
}

