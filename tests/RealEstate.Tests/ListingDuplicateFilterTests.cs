using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;
using RealEstate.Api.Services.Auth;
using RealEstate.Infrastructure;
using RealEstate.Infrastructure.Repositories;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Skrytí duplikátů ve vyhledávání – dotaz se musí přeložit do SQL
//  (ToQueryString nepotřebuje připojení k DB)
// ─────────────────────────────────────────────────────────────────
public class ListingDuplicateFilterTests
{
    private static ListingService CreateService()
    {
        var options = new DbContextOptionsBuilder<RealEstateDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        var ctx = new RealEstateDbContext(options);
        return new ListingService(new ListingRepository(ctx), ctx, new CurrentUser());
    }

    private static string WhereClause(ListingFilterDto filter)
    {
        var sql = CreateService().BuildFilteredQuery(filter).ToQueryString();
        return sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
    }

    [Fact]
    public void SourceFilter_HidesDuplicateOnlyWhenPrimaryMatchesToo()
    {
        var where = WhereClause(new ListingFilterDto { SourceCodes = ["SREALITY"] });

        Assert.Contains("duplicate_of_listing_id IS NULL OR NOT EXISTS", where);
        // Filtr zdroje se musí uplatnit na výsledek i na primární kopii v poddotazu
        Assert.Equal(2, where.Split("= ANY (@filter_SourceCodes)").Length - 1);
    }

    [Fact]
    public void SearchText_IsAppliedToPrimaryLookupToo()
    {
        var where = WhereClause(new ListingFilterDto { SearchText = "Práče" });

        var subquery = where[where.IndexOf("NOT EXISTS", StringComparison.Ordinal)..];
        Assert.Contains("@trimmed", subquery);
    }

    [Fact]
    public void DefaultSearch_ReturnsOnlyActiveListings()
    {
        var where = WhereClause(new ListingFilterDto { IncludeDuplicates = true });

        Assert.Contains("is_active", where);
        Assert.DoesNotContain("deactivated_at", where);
    }

    [Fact]
    public void DeactivatedSince_ReturnsInactiveListingsWithNoActiveCopyInGroup()
    {
        var where = WhereClause(new ListingFilterDto { DeactivatedSince = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), IncludeDuplicates = true });

        Assert.Contains("NOT (l.is_active)", where);
        Assert.Contains("deactivated_at >= @", where);
        // žádná aktivní kopie ve skupině duplicit (kořen = duplicate_of_listing_id nebo vlastní id)
        Assert.Contains("NOT EXISTS", where);
        Assert.Contains("COALESCE(l.duplicate_of_listing_id, l.id)", where);
    }

    [Fact]
    public void IncludeDuplicates_SkipsDuplicateFilter()
    {
        var where = WhereClause(new ListingFilterDto { IncludeDuplicates = true });

        Assert.DoesNotContain("duplicate_of_listing_id", where);
    }
}
