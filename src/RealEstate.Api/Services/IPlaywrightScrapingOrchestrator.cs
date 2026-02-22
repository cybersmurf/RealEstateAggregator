using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

/// <summary>
/// Orchestrates Playwright-based scraping jobs.
/// </summary>
public interface IPlaywrightScrapingOrchestrator
{
    /// <summary>
    /// Runs a Playwright scraping job for specified sources.
    /// </summary>
    /// <param name="request">Scraping trigger request with source codes and options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with job ID and status</returns>
    Task<ScrapeTriggerResultDto> RunAsync(
        ScrapeTriggerDto request,
        CancellationToken cancellationToken);
}
