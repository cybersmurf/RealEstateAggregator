using System.Net.Http.Json;
using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

public sealed class ScrapingService : IScrapingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ScrapingService> _logger;

    public ScrapingService(
        IHttpClientFactory httpClientFactory,
        ILogger<ScrapingService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("ScraperApi");
        _logger = logger;
    }

    public async Task<ScrapeTriggerResultDto> TriggerScrapeAsync(
        ScrapeTriggerDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/v1/scrape/run",
                request,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result =
                await response.Content.ReadFromJsonAsync<ScrapeTriggerResultDto>(cancellationToken: cancellationToken)
                ?? new ScrapeTriggerResultDto
                {
                    JobId = Guid.Empty,
                    Status = "Failed",
                    Message = "Empty response from scraper API."
                };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger scraping job.");
            return new ScrapeTriggerResultDto
            {
                JobId = Guid.Empty,
                Status = "Failed",
                Message = ex.Message
            };
        }
    }
}
