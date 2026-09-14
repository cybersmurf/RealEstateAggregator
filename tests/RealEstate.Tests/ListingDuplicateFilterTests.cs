using Microsoft.EntityFrameworkCore;
using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;
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
        return new ListingService(new ListingRepository(ctx), ctx);
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
    public void IncludeDuplicates_SkipsDuplicateFilter()
    {
        var where = WhereClause(new ListingFilterDto { IncludeDuplicates = true });

        Assert.DoesNotContain("duplicate_of_listing_id", where);
    }
}
