namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Reprezentuje kompletní detail inzerátu REMAX.
/// </summary>
public sealed class RemaxDetailResult
{
    public string SourceCode { get; set; } = "REMAX";
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public string? PriceNote { get; set; }
    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
    public string? PropertyType { get; set; }
    public string? OfferType { get; set; } = "Sale";
    public List<string> PhotoUrls { get; set; } = new();
}
