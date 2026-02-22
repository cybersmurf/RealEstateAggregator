namespace RealEstate.Infrastructure.Scraping.Remax;

/// <summary>
/// Reprezentuje jeden inzerát z list stránky REMAX.
/// </summary>
public sealed class RemaxListItem
{
    public string Title { get; set; } = string.Empty;
    public string DetailUrl { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public double? AreaBuiltUp { get; set; }
    public double? AreaLand { get; set; }
}
