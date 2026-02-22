using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RealEstate.Domain.Repositories;
using RealEstate.Infrastructure.Scraping;
using RealEstate.Infrastructure.Scraping.Remax;

namespace RealEstate.Api.Services;

public interface IRemaxZnojmoImportService
{
    Task RunAsync(CancellationToken ct = default);
}

public sealed class RemaxZnojmoImportService : IRemaxZnojmoImportService
{
    private readonly ILogger<RemaxZnojmoImportService> _logger;
    private readonly IListingRepository _listingRepository;
    private readonly ILoggerFactory _loggerFactory;

    public RemaxZnojmoImportService(
        ILogger<RemaxZnojmoImportService> logger,
        IListingRepository listingRepository,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _listingRepository = listingRepository;
        _loggerFactory = loggerFactory;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Spouštím REMAX Znojmo import job");

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

        // TODO: Získat Source ID pro REMAX z databáze nebo konfigurace
        // Pro MVP použijeme placeholder - v produkci se musí načíst z DB
        var sourceId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var importerLogger = _loggerFactory.CreateLogger<RemaxZnojmoImporter>();
        var importer = new RemaxZnojmoImporter(browser, _listingRepository, importerLogger);
        await importer.ImportAsync(sourceId, ct);

        playwright.Dispose();
        _logger.LogInformation("REMAX Znojmo import job dokončen");
    }
}
