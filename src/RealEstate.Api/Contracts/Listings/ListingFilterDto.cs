namespace RealEstate.Api.Contracts.Listings;

public sealed class ListingFilterDto
{
    public List<string>? SourceCodes { get; set; }
    public string? Region { get; set; }
    public string? District { get; set; }
    public string? Municipality { get; set; }

    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }

    public double? AreaBuiltUpMin { get; set; }
    public double? AreaBuiltUpMax { get; set; }

    public double? AreaLandMin { get; set; }
    public double? AreaLandMax { get; set; }

    public string? PropertyType { get; set; }
    public string? OfferType { get; set; }
    public string? Disposition { get; set; }    // "1+1", "2+kk", atd.

    public string? UserStatus { get; set; }
    public DateTime? OnlyNewSince { get; set; }

    public string? SearchText { get; set; }

    /// <summary>Sloupec řazení: "price", "area", "land", "date", "title", "location"</summary>
    public string? SortBy { get; set; }
    /// <summary>Sestupně = true, vzestupně = false (výchozí)</summary>
    public bool SortDescending { get; set; } = false;

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
