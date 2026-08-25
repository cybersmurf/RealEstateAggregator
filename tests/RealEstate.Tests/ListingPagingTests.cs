using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  NormalizePaging – strop stránkování pro veřejné /api/listings/search
// ─────────────────────────────────────────────────────────────────
public class ListingPagingTests
{
    private const int MaxSearch = 200;

    [Theory]
    [InlineData(1_000_000, MaxSearch)]
    [InlineData(5_000, MaxSearch)]
    [InlineData(201, MaxSearch)]
    [InlineData(200, 200)]
    [InlineData(50, 50)]
    [InlineData(1, 1)]
    public void PageSize_IsClampedToMax(int requested, int expected)
    {
        var filter = new ListingFilterDto { PageSize = requested };

        ListingService.NormalizePaging(filter, MaxSearch);

        Assert.Equal(expected, filter.PageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositivePageSize_FallsBackToOne(int requested)
    {
        var filter = new ListingFilterDto { PageSize = requested };

        ListingService.NormalizePaging(filter, MaxSearch);

        Assert.Equal(1, filter.PageSize);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public void Page_IsNeverBelowOne(int requested, int expected)
    {
        var filter = new ListingFilterDto { Page = requested };

        ListingService.NormalizePaging(filter, MaxSearch);

        Assert.Equal(expected, filter.Page);
    }

    [Fact]
    public void ExportLimit_AllowsLargerPageThanSearch()
    {
        var filter = new ListingFilterDto { PageSize = 5_000 };

        ListingService.NormalizePaging(filter, 5_000);

        Assert.Equal(5_000, filter.PageSize);
    }
}
