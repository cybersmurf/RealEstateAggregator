using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using RealEstate.Domain.Entities;
using RealEstate.Domain.Enums;
using RealEstate.Domain.Repositories;

namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Orchestrátor pro import REMAX Znojmo listingů.
/// Kombinuje list scraper, detail scraper a uložení do databáze.
/// </summary>
public sealed class RemaxZnojmoImporter
{
    private readonly IBrowser _browser;
    private readonly IListingRepository _listingRepository;
    private readonly ILogger<RemaxZnojmoImporter> _logger;

    public RemaxZnojmoImporter(
        IBrowser browser,
        IListingRepository listingRepository,
        ILogger<RemaxZnojmoImporter> logger)
    {
        _browser = browser;
        _listingRepository = listingRepository;
        _logger = logger;
    }

    public async Task ImportAsync(Guid sourceId, CancellationToken ct = default)
    {
        var listUrl =
            "https://www.remax-czech.cz/reality/domy-a-vily/prodej/jihomoravsky-kraj/znojmo/";

        _logger.LogInformation("Spouštím REMAX Znojmo import z URL: {Url}", listUrl);

        var listScraper = new RemaxListScraper(_browser);
        var detailScraper = new RemaxDetailScraper(_browser);

        var listItems = await listScraper.ScrapeListAsync(listUrl, ct);
        _logger.LogInformation("Načteno {Count} inzerátů ze seznamu", listItems.Count);

        var imported = 0;
        var failed = 0;

        foreach (var item in listItems)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                _logger.LogDebug("Zpracovávám detail: {Url}", item.DetailUrl);
                
                var detail = await detailScraper.ScrapeDetailAsync(item, ct);
                var entity = MapToListingEntity(sourceId, detail);
                
                await _listingRepository.UpsertAsync(entity, ct);
                imported++;
                
                _logger.LogDebug("Uložen inzerát: {Title}", detail.Title);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Chyba při zpracování inzerátu: {Url}", item.DetailUrl);
            }
        }

        _logger.LogInformation(
            "REMAX Znojmo import dokončen. Úspěšně: {Imported}, Chyby: {Failed}",
            imported, failed);
    }

    private static Listing MapToListingEntity(Guid sourceId, RemaxDetailResult detail)
    {
        var externalId = ExtractExternalId(detail.Url);

        return new Listing
        {
            SourceId = sourceId,
            ExternalId = externalId,
            Url = detail.Url,
            Title = detail.Title,
            Description = detail.Description,
            LocationText = detail.LocationText,
            Price = detail.Price,
            PriceNote = detail.PriceNote,
            AreaBuiltUp = (double?)detail.AreaBuiltUp,
            AreaLand = (double?)detail.AreaLand,
            PropertyType = PropertyType.House,
            OfferType = OfferType.Sale,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            IsActive = true,
            Photos = detail.PhotoUrls
                .Select((url, index) => new ListingPhoto
                {
                    OriginalUrl = url,
                    Order = index,
                    CreatedAt = DateTime.UtcNow
                })
                .ToList()
        };
    }

    private static string ExtractExternalId(string url)
    {
        // REMAX používá různé formáty URL, pokud není jasné ID, použij hash URL
        // Příklad: https://www.remax-czech.cz/nemovitost/123456-dum-znojmo
        
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
        {
            var lastSegment = segments[^1];
            
            // Zkus najít číselné ID na začátku segmentu
            var match = System.Text.RegularExpressions.Regex.Match(lastSegment, @"^(\d+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // Fallback: použij celou URL jako ID
        return url;
    }
}
