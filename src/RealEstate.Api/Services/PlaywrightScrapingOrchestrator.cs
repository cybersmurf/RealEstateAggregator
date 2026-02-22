using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

/// <summary>
/// Orchestrates Playwright-based scraping jobs using .NET Playwright.
/// This is an alternative to the Python FastAPI scraper.
/// </summary>
public sealed class PlaywrightScrapingOrchestrator : IPlaywrightScrapingOrchestrator
{
    private readonly IRemaxScrapingService _remaxScrapingService;
    private readonly ILogger<PlaywrightScrapingOrchestrator> _logger;

    public PlaywrightScrapingOrchestrator(
        IRemaxScrapingService remaxScrapingService,
        ILogger<PlaywrightScrapingOrchestrator> logger)
    {
        _remaxScrapingService = remaxScrapingService;
        _logger = logger;
    }

    public async Task<ScrapeTriggerResultDto> RunAsync(
        ScrapeTriggerDto request,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        _logger.LogInformation(
            "Starting Playwright scraping job {JobId} for sources: {Sources}, FullRescan: {FullRescan}",
            jobId,
            request.SourceCodes != null ? string.Join(", ", request.SourceCodes) : "ALL",
            request.FullRescan);

        try
        {
            // Determine which sources to scrape
            var sourceCodes = request.SourceCodes ?? new List<string> { "REMAX", "MMR", "PRODEJMETO" };
            
            // REMAX scraping - s volitelným profilem pro specifické regiony/okresy
            if (sourceCodes.Contains("REMAX"))
            {
                // Default: okres Znojmo (Jihomoravský kraj 116, Znojmo 3713)
                var profile = request.RemaxProfile ?? new RemaxScrapingProfileDto
                {
                    Name = "Default: Okres Znojmo",
                    RegionId = 116,
                    DistrictId = 3713,
                    PropertyTypeMask = 6,
                    PriceMax = 7_500_000,
                    SearchType = 2
                };
                
                _logger.LogInformation("Running REMAX scraping with profile: {ProfileName}", profile.Name);
                await _remaxScrapingService.RunAsync(profile, cancellationToken);
            }
            else
            {
                _logger.LogInformation("No matching scrapers for requested sources: {Sources}", 
                    string.Join(", ", sourceCodes));
            }

            _logger.LogInformation("Playwright scraping job {JobId} completed successfully", jobId);

            return new ScrapeTriggerResultDto
            {
                JobId = jobId,
                Status = "Succeeded",
                Message = $"Playwright scraping job completed for sources: {string.Join(", ", sourceCodes)}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playwright scraping job {JobId} failed", jobId);

            return new ScrapeTriggerResultDto
            {
                JobId = jobId,
                Status = "Failed",
                Message = $"Scraping failed: {ex.Message}"
            };
        }
    }
}
