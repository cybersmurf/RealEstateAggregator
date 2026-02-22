using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

public interface IScrapingService
{
    Task<ScrapeTriggerResultDto> TriggerScrapeAsync(
        ScrapeTriggerDto request,
        CancellationToken cancellationToken);
}
