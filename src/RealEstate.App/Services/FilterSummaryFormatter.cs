using System.Globalization;
using RealEstate.Api.Contracts.Listings;

namespace RealEstate.App.Services;

/// <summary>
/// Lidsky čitelný souhrn filtru, např. „Dům · Prodej · Znojmo · do 5 000 000 Kč · ≥ 100 m²“.
/// Používá se jako výchozí název uloženého hledání a v jeho výpisu.
/// </summary>
public static class FilterSummaryFormatter
{
    private static readonly CultureInfo Cs = CultureInfo.GetCultureInfo("cs-CZ");

    public static string Summarize(ListingFilterDto? filter)
    {
        if (filter is null) return "Všechny inzeráty";

        var parts = new List<string>();

        if (PropertyTypeLabel(filter.PropertyType) is { } property) parts.Add(property);
        if (OfferTypeLabel(filter.OfferType) is { } offer) parts.Add(offer);
        if (!string.IsNullOrWhiteSpace(filter.Disposition)) parts.Add(filter.Disposition);

        var location = FirstNonEmpty(filter.Municipality, filter.District, filter.Region);
        if (location is not null) parts.Add(location);

        if (Range(filter.PriceMin, filter.PriceMax, Price) is { } price) parts.Add(price);
        if (Range(filter.AreaBuiltUpMin, filter.AreaBuiltUpMax, Area) is { } area) parts.Add(area);
        if (Range(filter.AreaLandMin, filter.AreaLandMax, Area) is { } land) parts.Add($"pozemek {land}");
        if (Range(filter.RoomsMin, filter.RoomsMax, r => r.ToString(Cs)) is { } rooms) parts.Add($"{rooms} pokojů");

        if (filter.Conditions is { Count: > 0 }) parts.Add(string.Join("/", filter.Conditions));
        if (filter.ConstructionTypes is { Count: > 0 }) parts.Add(string.Join("/", filter.ConstructionTypes));
        if (filter.SourceCodes is { Count: > 0 }) parts.Add($"zdroje: {string.Join(", ", filter.SourceCodes)}");
        if (!string.IsNullOrWhiteSpace(filter.SearchText)) parts.Add($"„{filter.SearchText.Trim()}“");
        if (filter.BboxLatMin is not null || filter.BboxLatMax is not null || filter.BboxLonMin is not null || filter.BboxLonMax is not null)
            parts.Add("výřez mapy");

        return parts.Count == 0 ? "Všechny inzeráty" : string.Join(" · ", parts);
    }

    public static string? PropertyTypeLabel(string? type) => type switch
    {
        "House" => "Dům",
        "Apartment" => "Byt",
        "Land" => "Pozemek",
        "Cottage" => "Chata / chalupa",
        "Commercial" => "Komerční",
        "Industrial" => "Průmyslový",
        "Garage" => "Garáž",
        "Other" => "Ostatní",
        null or "" => null,
        _ => type,
    };

    public static string? OfferTypeLabel(string? type) => type switch
    {
        "Sale" => "Prodej",
        "Rent" => "Pronájem",
        "Auction" => "Dražba",
        null or "" => null,
        _ => type,
    };

    private static string? Range<T>(T? min, T? max, Func<T, string> format) where T : struct
    {
        if (min is null && max is null) return null;
        if (min is not null && max is not null) return $"{format(min.Value)} – {format(max.Value)}";
        return min is not null ? $"≥ {format(min.Value)}" : $"≤ {format(max!.Value)}";
    }

    private static string Price(decimal value) =>
        string.Format(Cs, "{0:N0} Kč", value).Replace(' ', ' ');

    private static string Area(double value) =>
        string.Format(Cs, "{0:N0} m²", value).Replace(' ', ' ');

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
