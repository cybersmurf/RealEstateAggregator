using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RealEstate.Api.Contracts.Scraping;
using RealEstate.Domain.Repositories;
using RealEstate.Infrastructure.Scraping.Remax;

namespace RealEstate.Api.Services;

/// <summary>
/// Generický service pro REMAX scraping s konfigurovatelem profilu.
/// Umožňuje scrapovat libovolné REMAX regiony/okresy/města s filtry.
/// </summary>
public interface IRemaxScrapingService
{
    Task RunAsync(RemaxScrapingProfileDto profile, CancellationToken ct = default);
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
    /// Spustí scraping se zadaným REMAX profilem.
    /// Profil obsahuje konfiguraci: region, okres, město, filtry. ceny, typy nemovitostí, atd.
    /// </summary>
    public async Task RunAsync(RemaxScrapingProfileDto profile, CancellationToken ct = default)
    {
        // Pokud je direktní URL, použij ji přímo; jinak vytvořit URL z parametrů
        string searchUrl = profile.DirectUrl ?? BuildSearchUrl(profile);
        
        _logger.LogInformation("Spouštím REMAX scraping profilu '{ProfileName}' z URL: {Url}", 
            profile.Name, searchUrl);

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

        var importerLogger = _loggerFactory.CreateLogger<RemaxImporter>();
        var importer = new RemaxImporter(browser, _listingRepository, importerLogger);
        await importer.ImportAsync(source.Id, searchUrl, ct);

        playwright.Dispose();
        _logger.LogInformation("REMAX scraping dokončen pro profil '{ProfileName}'", profile.Name);
    }

    /// <summary>
    /// Vytvořit REMAX search URL z параметrů profilu.
    /// Vrací vyhledávací URL s odpovídajícími parametry.
    /// </summary>
    private static string BuildSearchUrl(RemaxScrapingProfileDto profile)
    {
        var baseUrl = "https://www.remax-czech.cz/reality/vyhledavani/";
        var queryParams = new Dictionary<string, string>();

        // Přidej hledání typ (fulltext nebo region-based)
        queryParams["hledani"] = profile.SearchType.ToString();

        // Fulltext vyhledávání (desc_text)
        if (!string.IsNullOrEmpty(profile.SearchText) && profile.SearchType == 1)
        {
            queryParams["desc_text"] = Uri.EscapeDataString(profile.SearchText);
        }

        // Region + okres konfiguraci
        if (profile.RegionId.HasValue && profile.DistrictId.HasValue && profile.SearchType == 2)
        {
            // regions[116][3713]=on (Region ID => District ID)
            queryParams[$"regions[{profile.RegionId}][{profile.DistrictId}]"] = "on";
        }

        // Ceny
        if (profile.PriceMax.HasValue)
        {
            queryParams["price_to"] = profile.PriceMax.Value.ToString();
        }
        if (profile.PriceMin.HasValue)
        {
            queryParams["price_from"] = profile.PriceMin.Value.ToString();
        }

        // Typ nemovitosti: types[6]=domy, types[1]=byty, atd.
        queryParams[$"types[{profile.PropertyTypeMask}]"] = "on";

        // Sestav URL
        var queryString = string.Join("&", queryParams.Select(kvp => 
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return $"{baseUrl}?{queryString}";
    }
}

