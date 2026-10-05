using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

public interface IScrapingService
{
    Task<ScrapeTriggerResultDto> TriggerScrapeAsync(
        ScrapeTriggerDto request,
        CancellationToken cancellationToken);

    /// <summary>Stav jobu; null = scraper ho nezná.</summary>
    Task<ScrapeJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Nejnovější joby (nejnovější první).</summary>
    Task<IReadOnlyList<ScrapeJobDto>> GetRecentJobsAsync(int limit, CancellationToken cancellationToken);
}
