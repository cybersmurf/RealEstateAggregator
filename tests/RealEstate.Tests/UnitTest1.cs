using System.Reflection;
using RealEstate.Domain.Enums;
using RealEstate.Api.Services;
using ApiContracts = RealEstate.Api.Contracts.Listings;

namespace RealEstate.Tests;

// Lokální kopie záznamu – SourceDto v App projektu nelze přímo referencovat
// z testů kvůli kolizi namespace (App sdílí typy s Api).
// Testy ověřují chování record typů obecně.
internal sealed record SourceDto(Guid Id, string Code, string Name, string BaseUrl, bool IsActive);

// ─────────────────────────────────────────────────────────────────
//  Enum konverze – DB ukládá anglické názvy ("House", "Sale" …)
// ─────────────────────────────────────────────────────────────────
public class EnumStringConversionTests
{
    [Theory]
    [InlineData(PropertyType.House,       "House")]
    [InlineData(PropertyType.Apartment,   "Apartment")]
    [InlineData(PropertyType.Land,        "Land")]
    [InlineData(PropertyType.Cottage,     "Cottage")]
    [InlineData(PropertyType.Commercial,  "Commercial")]
    [InlineData(PropertyType.Industrial,  "Industrial")]
    [InlineData(PropertyType.Garage,      "Garage")]
    [InlineData(PropertyType.Other,       "Other")]
    public void PropertyType_ToString_ReturnsEnglishDatabaseValue(PropertyType pt, string expected)
    {
        Assert.Equal(expected, pt.ToString());
    }

    [Theory]
    [InlineData(OfferType.Sale,    "Sale")]
    [InlineData(OfferType.Rent,    "Rent")]
    [InlineData(OfferType.Auction, "Auction")]   // přidáno Session 5
    public void OfferType_ToString_ReturnsEnglishDatabaseValue(OfferType ot, string expected)
    {
        Assert.Equal(expected, ot.ToString());
    }

    [Theory]
    [InlineData("House",      PropertyType.House)]
    [InlineData("Apartment",  PropertyType.Apartment)]
    [InlineData("Land",       PropertyType.Land)]
    [InlineData("Cottage",    PropertyType.Cottage)]
    [InlineData("Commercial", PropertyType.Commercial)]
    [InlineData("Industrial", PropertyType.Industrial)]
    [InlineData("Garage",     PropertyType.Garage)]
    [InlineData("Other",      PropertyType.Other)]
    public void PropertyType_ParseFromString_RoundTrips(string value, PropertyType expected)
    {
        Assert.Equal(expected, Enum.Parse<PropertyType>(value));
    }

    [Theory]
    [InlineData("Sale",    OfferType.Sale)]
    [InlineData("Rent",    OfferType.Rent)]
    [InlineData("Auction", OfferType.Auction)]   // přidáno Session 5
    public void OfferType_ParseFromString_RoundTrips(string value, OfferType expected)
    {
        Assert.Equal(expected, Enum.Parse<OfferType>(value));
    }
}

// ─────────────────────────────────────────────────────────────────
//  NormalizeStatus (via reflection – metoda je private static)
// ─────────────────────────────────────────────────────────────────
public class NormalizeStatusTests
{
    private static string Invoke(string? status)
    {
        var method = typeof(ListingService).GetMethod(
            "NormalizeStatus",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [status])!;
    }

    [Theory]
    [InlineData(null,         "New")]
    [InlineData("",           "New")]
    [InlineData("   ",        "New")]
    [InlineData("Unknown",    "New")]
    [InlineData("invalid",    "New")]
    [InlineData("Auction",    "New")]   // OfferType.Auction ≠ UserStatus → musí vrátit New
    public void NullOrUnknown_ReturnsNew(string? input, string expected)
        => Assert.Equal(expected, Invoke(input));

    [Theory]
    [InlineData("New",       "New")]
    [InlineData("Liked",     "Liked")]
    [InlineData("Disliked",  "Disliked")]
    [InlineData("ToVisit",   "ToVisit")]
    [InlineData("Visited",   "Visited")]
    public void KnownStatuses_ReturnThemselves(string input, string expected)
        => Assert.Equal(expected, Invoke(input));

    [Theory]
    [InlineData("new",      "new")]
    [InlineData("LIKED",    "LIKED")]
    [InlineData("toVisit",  "toVisit")]
    public void CaseInsensitive_ReturnsInputAsIs(string input, string expected)
        => Assert.Equal(expected, Invoke(input));
}

// ─────────────────────────────────────────────────────────────────
//  SourceDto record – rovnost a vlastnosti
// ─────────────────────────────────────────────────────────────────
public class SourceDtoTests
{
    [Fact]
    public void SourceDto_RecordEquality_Works()
    {
        var id = Guid.NewGuid();
        var a = new SourceDto(id, "REMAX", "RE/MAX", "https://remax.cz", true);
        var b = new SourceDto(id, "REMAX", "RE/MAX", "https://remax.cz", true);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void SourceDto_DifferentId_NotEqual()
    {
        var a = new SourceDto(Guid.NewGuid(), "REMAX", "RE/MAX", "https://remax.cz", true);
        var b = new SourceDto(Guid.NewGuid(), "REMAX", "RE/MAX", "https://remax.cz", true);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void SourceDto_WithExpression_CreatesModifiedCopy()
    {
        var original = new SourceDto(Guid.NewGuid(), "MMR", "M&M Reality", "https://mmreality.cz", true);
        var inactive = original with { IsActive = false };

        Assert.True(original.IsActive);
        Assert.False(inactive.IsActive);
        Assert.Equal(original.Id, inactive.Id);
    }
}

// ─────────────────────────────────────────────────────────────────
//  ListingFilterDto – výchozí hodnoty a nastavení
// ─────────────────────────────────────────────────────────────────
public class ListingFilterDtoTests
{
    [Fact]
    public void DefaultFilter_HasCorrectPagingDefaults()
    {
        var filter = new ApiContracts.ListingFilterDto();

        Assert.Equal(1, filter.Page);
        Assert.Equal(50, filter.PageSize);
    }

    [Fact]
    public void DefaultFilter_AllFiltersAreNull()
    {
        var filter = new ApiContracts.ListingFilterDto();

        Assert.Null(filter.SearchText);
        Assert.Null(filter.PropertyType);
        Assert.Null(filter.OfferType);
        Assert.Null(filter.SourceCodes);
        Assert.Null(filter.PriceMin);
        Assert.Null(filter.PriceMax);
    }

    [Fact]
    public void Filter_CanSetAllProperties()
    {
        var filter = new ApiContracts.ListingFilterDto
        {
            Page = 2,
            PageSize = 25,
            PropertyType = "House",
            OfferType = "Sale",
            PriceMin = 1_000_000m,
            PriceMax = 5_000_000m,
            SearchText = "Znojmo centrum"
        };

        Assert.Equal(2, filter.Page);
        Assert.Equal(25, filter.PageSize);
        Assert.Equal("House", filter.PropertyType);
        Assert.Equal("Sale", filter.OfferType);
        Assert.Equal(1_000_000m, filter.PriceMin);
        Assert.Equal(5_000_000m, filter.PriceMax);
        Assert.Equal("Znojmo centrum", filter.SearchText);
    }
}
