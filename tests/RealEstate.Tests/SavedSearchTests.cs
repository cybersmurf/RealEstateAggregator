using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services.Notifications;
using RealEstate.Api.Services.SavedSearches;
using RealEstate.Domain.Entities;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Uložená hledání – formát digestu, kontrola tarifu bez requestu,
//  normalizace uloženého filtru.
// ─────────────────────────────────────────────────────────────────
public class SavedSearchTests
{
    private const string BaseUrl = "https://realestate.sudata.eu";
    private static readonly Guid ListingId = new("11111111-2222-3333-4444-555555555555");

    // ── NotificationMessageBuilder ──────────────────────────────────

    [Theory]
    [InlineData(5_000_000, 4_000_000, 20)]
    [InlineData(3_990_000, 3_790_000, 5)]
    [InlineData(1_000_000, 995_000, 1)]
    [InlineData(1_000_000, 996_000, 0)]
    [InlineData(0, 100, 0)]
    public void DropPercent_RoundsToWholePercent(decimal oldPrice, decimal newPrice, int expected)
    {
        Assert.Equal(expected, NotificationMessageBuilder.DropPercent(oldPrice, newPrice));
    }

    [Theory]
    [InlineData("https://realestate.sudata.eu")]
    [InlineData("https://realestate.sudata.eu/")]
    public void ListingUrl_IsAbsoluteWithoutDoubleSlash(string baseUrl)
    {
        var url = NotificationMessageBuilder.ListingUrl(baseUrl, ListingId);

        Assert.Equal($"https://realestate.sudata.eu/listings/{ListingId}", url);
    }

    [Fact]
    public void ListingUrl_EmptyBase_FallsBackToLocalhost()
    {
        Assert.StartsWith("http://localhost:5002/listings/", NotificationMessageBuilder.ListingUrl("", ListingId));
        Assert.Equal("http://localhost:5002/saved-searches", NotificationMessageBuilder.SavedSearchesUrl(" "));
    }

    [Fact]
    public void BuildEmailHtml_ContainsPriceDropWithPercentAndLinks()
    {
        var drops = new List<NotificationPriceDropItem>
        {
            new(ListingId, "RD 4+kk Dyje", 5_000_000, 4_500_000, "Dyje, okres Znojmo"),
        };

        var html = NotificationMessageBuilder.BuildEmailHtml("Domy Znojmo", [], drops, BaseUrl);

        Assert.Contains("Domy Znojmo", html);
        Assert.Contains("Zlevnění (1)", html);
        Assert.Contains("−10 %", html);
        Assert.Contains("5 000 000 Kč", html);
        Assert.Contains("4 500 000 Kč", html);
        Assert.Contains($"href=\"{BaseUrl}/listings/{ListingId}\"", html);
        Assert.Contains($"href=\"{BaseUrl}/saved-searches\"", html);
        Assert.DoesNotContain("Nové inzeráty", html);
    }

    [Fact]
    public void BuildEmailHtml_EscapesUserContent()
    {
        var items = new List<NotificationListingItem>
        {
            new(ListingId, "<script>alert(1)</script> Byt 2+kk", 2_500_000, "Brno & okolí"),
        };

        var html = NotificationMessageBuilder.BuildEmailHtml("Test <b>", items, [], BaseUrl);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Brno &amp; okolí", html);
        Assert.Contains("Test &lt;b&gt;", html);
    }

    [Fact]
    public void BuildEmailText_ListsNewListingsWithUrls()
    {
        var items = new List<NotificationListingItem>
        {
            new(ListingId, "Chalupa Vranov", null, "Vranov nad Dyjí"),
        };

        var text = NotificationMessageBuilder.BuildEmailText("Chalupy", items, [], BaseUrl);

        Assert.Contains("Nové inzeráty (1):", text);
        Assert.Contains("Chalupa Vranov | cena neuvedena | Vranov nad Dyjí", text);
        Assert.Contains($"{BaseUrl}/listings/{ListingId}", text);
        Assert.Contains($"{BaseUrl}/saved-searches", text);
    }

    [Fact]
    public void BuildTelegramHtml_TruncatesLongListAndSummarizesRest()
    {
        var items = Enumerable.Range(0, 40)
            .Select(i => new NotificationListingItem(Guid.NewGuid(), $"Inzerát {i}", 1_000_000 + i, "Znojmo"))
            .ToList();

        var html = NotificationMessageBuilder.BuildTelegramHtml("Znojmo", items, [], BaseUrl);

        Assert.Contains("<b>Nové inzeráty (40)</b>", html);
        Assert.Contains("… a dalších 25", html);
        Assert.Contains("Inzerát 14", html);
        Assert.DoesNotContain("Inzerát 15", html);
        Assert.DoesNotContain("<ul>", html);
        Assert.True(html.Length < 4096);
    }

    [Theory]
    [InlineData(0, 0, "Byty Brno: bez změn")]
    [InlineData(1, 0, "Byty Brno: 1 nový inzerát")]
    [InlineData(3, 1, "Byty Brno: 3 nových inzerátů, 1 zlevnění")]
    [InlineData(0, 2, "Byty Brno: 2 zlevnění")]
    public void BuildSubject_UsesCzechPlurals(int newCount, int dropCount, string expected)
    {
        Assert.Equal(expected, NotificationMessageBuilder.BuildSubject("Byty Brno", newCount, dropCount));
    }

    // ── PlanAccess ──────────────────────────────────────────────────

    private static User UserWith(string plan, DateTime? validUntil = null, bool isAdmin = false) => new()
    {
        Email = "test@example.com",
        Plan = plan,
        PlanValidUntil = validUntil,
        IsAdmin = isAdmin,
    };

    [Fact]
    public void HasPlan_ExpiredHledac_IsTreatedAsFree()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var user = UserWith(UserPlans.Hledac, now.AddDays(-1));

        Assert.False(PlanAccess.HasPlan(user, UserPlans.Hledac, now));
        Assert.Equal(UserPlans.Free, PlanAccess.EffectivePlan(user, now));
        Assert.Equal(0, PlanAccess.MaxSavedSearches(user, now));
    }

    [Fact]
    public void HasPlan_ValidHledac_Passes()
    {
        var now = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        var user = UserWith(UserPlans.Hledac, now.AddDays(30));

        Assert.True(PlanAccess.HasPlan(user, UserPlans.Hledac, now));
        Assert.False(PlanAccess.HasPlan(user, UserPlans.Profi, now));
        Assert.Equal(10, PlanAccess.MaxSavedSearches(user, now));
    }

    [Fact]
    public void HasPlan_WithoutExpiry_IsPermanent()
    {
        var user = UserWith(UserPlans.Profi);

        Assert.True(PlanAccess.HasPlan(user, UserPlans.Hledac));
        Assert.True(PlanAccess.HasPlan(user, UserPlans.Profi));
        Assert.Equal(50, PlanAccess.MaxSavedSearches(user));
    }

    [Fact]
    public void HasPlan_Admin_AlwaysPassesAndHasNoLimit()
    {
        var user = UserWith(UserPlans.Free, isAdmin: true);

        Assert.True(PlanAccess.HasPlan(user, UserPlans.Profi));
        Assert.Null(PlanAccess.MaxSavedSearches(user));
    }

    [Fact]
    public void HasPlan_Free_Fails()
    {
        Assert.False(PlanAccess.HasPlan(UserWith(UserPlans.Free), UserPlans.Hledac));
    }

    // ── Normalizace uloženého filtru ────────────────────────────────

    [Fact]
    public void SerializeFilter_StripsPagingAndUserStatus_UsesCamelCase()
    {
        var filter = new ListingFilterDto
        {
            PropertyType = "House",
            Municipality = "Znojmo",
            PriceMax = 5_000_000,
            Page = 7,
            PageSize = 200,
            UserStatus = "Interesting",
        };

        var json = SavedSearchService.SerializeFilter(filter);

        Assert.Contains("\"propertyType\":\"House\"", json);
        Assert.Contains("\"municipality\":\"Znojmo\"", json);
        Assert.Contains("\"page\":1", json);
        Assert.Contains("\"pageSize\":1", json);
        Assert.DoesNotContain("userStatus", json);
        Assert.DoesNotContain("PropertyType", json);
    }

    [Fact]
    public void DeserializeFilter_InvalidJson_FallsBackToEmptyFilter()
    {
        var filter = SavedSearchService.DeserializeFilter("{not json");

        Assert.Null(filter.PropertyType);
        Assert.Equal(1, filter.Page);
        Assert.Equal(1, filter.PageSize);
        Assert.Null(filter.UserStatus);
    }

    [Fact]
    public void DeserializeFilter_RoundTripsStoredFilter()
    {
        var json = SavedSearchService.SerializeFilter(new ListingFilterDto
        {
            OfferType = "Sale",
            AreaBuiltUpMin = 100,
            SourceCodes = ["SREALITY", "REMAX"],
            UserStatus = "Visited",
        });

        var filter = SavedSearchService.DeserializeFilter(json);

        Assert.Equal("Sale", filter.OfferType);
        Assert.Equal(100, filter.AreaBuiltUpMin);
        Assert.Equal(["SREALITY", "REMAX"], filter.SourceCodes);
        Assert.Null(filter.UserStatus);
    }
}
