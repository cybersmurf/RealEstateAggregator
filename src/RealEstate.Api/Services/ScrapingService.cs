using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RealEstate.Api.Contracts.Scraping;

namespace RealEstate.Api.Services;

public sealed class ScrapingService : IScrapingService
{
    /// <summary>Scraper (FastAPI) odpovídá snake_case – <c>job_id</c>; bez toho bylo JobId vždy prázdné.</summary>
    private static readonly JsonSerializerOptions ScraperJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

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
            _logger.LogInformation("Triggering scraper API at {BaseAddress}", _httpClient.BaseAddress);

            var response = await _httpClient.PostAsJsonAsync(
                "/v1/scrape/run",
                request,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var result =
                await response.Content.ReadFromJsonAsync<ScrapeTriggerResultDto>(ScraperJson, cancellationToken)
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

    public async Task<ScrapeJobDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync($"/v1/scrape/jobs/{jobId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScrapeJobDto>(ScraperJson, cancellationToken);
    }

    public async Task<IReadOnlyList<ScrapeJobDto>> GetRecentJobsAsync(int limit, CancellationToken cancellationToken)
        => await _httpClient.GetFromJsonAsync<List<ScrapeJobDto>>(
               $"/v1/scrape/jobs?limit={limit}", ScraperJson, cancellationToken)
           ?? [];
}
