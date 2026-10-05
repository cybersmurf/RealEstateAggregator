using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RealEstate.Api.Contracts.Scraping;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// Scraper (FastAPI) odpovídá snake_case – do 5. 10. 2026 se job_id nenačetlo a App neměla co sledovat
public class ScrapingServiceTests
{
    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://scraper:8001") };
    }

    private static ScrapingService Create(StubHandler handler) => new(new StubFactory(handler), NullLogger<ScrapingService>.Instance);

    [Fact]
    public async Task Trigger_ReadsSnakeCaseJobId()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"job_id":"4cf0eadf-994c-42f7-b91e-8efa8f9910a5","status":"Queued","message":"Scraping job enqueued."}""");

        var result = await Create(handler).TriggerScrapeAsync(new ScrapeTriggerDto(), CancellationToken.None);

        Assert.Equal(Guid.Parse("4cf0eadf-994c-42f7-b91e-8efa8f9910a5"), result.JobId);
        Assert.Equal("Queued", result.Status);
    }

    [Fact]
    public async Task GetJob_ReadsStatusAndError()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"job_id":"4cf0eadf-994c-42f7-b91e-8efa8f9910a5","source_codes":["BAZOS"],"full_rescan":true,"created_at":"2026-10-05T17:43:48.659873Z","status":"Failed","error_message":"timeout"}""");

        var job = await Create(handler).GetJobAsync(Guid.Parse("4cf0eadf-994c-42f7-b91e-8efa8f9910a5"), CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal("Failed", job.Status);
        Assert.Equal("timeout", job.ErrorMessage);
        Assert.True(job.FullRescan);
        Assert.Equal(["BAZOS"], job.SourceCodes!);
        Assert.Equal("/v1/scrape/jobs/4cf0eadf-994c-42f7-b91e-8efa8f9910a5", handler.LastPath);
    }

    [Fact]
    public async Task GetJob_UnknownJob_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"detail":"Job not found"}""");

        Assert.Null(await Create(handler).GetJobAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
