namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingStatsDto
{
    public int TotalCount { get; set; }
    public int ActiveSourceCount { get; set; }
    public List<SourceCountDto> CountsBySource { get; set; } = [];
}

public sealed class SourceCountDto
{
    public string SourceCode { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public int Count { get; set; }
}
