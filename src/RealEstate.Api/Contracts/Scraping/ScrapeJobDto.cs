namespace RealEstate.Api.Contracts.Scraping;

/// <summary>Stav scrape jobu ze scraperu (<c>/v1/scrape/jobs</c>). Status: Queued, Running, Succeeded, Failed.</summary>
public sealed record ScrapeJobDto(
    Guid JobId,
    List<string>? SourceCodes,
    bool FullRescan,
    DateTime CreatedAt,
    string Status,
    string? ErrorMessage);
