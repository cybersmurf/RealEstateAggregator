using RealEstate.Api.Contracts.Listings;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Historie cen za skupinu duplicit – Znojmo centrum: Sreality zlevnilo 18. 8. ze 7,43 na 6,90 mil.,
//  bazošová kopie (vložená 10. 8.) v detailu žádné zlevnění neukazovala a tvrdila „51 dní na trhu".
// ─────────────────────────────────────────────────────────────────
public class PriceHistoryMergeTests
{
    private static PriceHistoryDto Row(decimal price, string date, string source)
        => new(price, DateTimeOffset.Parse(date + "T00:00:00Z"), source);

    [Fact]
    public void GroupHistory_CollapsesToOneSeries()
    {
        var merged = ListingService.MergePriceHistory(
        [
            Row(7_427_735m, "2026-06-13", "SREALITY"),
            Row(7_427_735m, "2026-06-15", "IDNES"),
            Row(7_427_735m, "2026-08-10", "BAZOS"),
            Row(6_897_485m, "2026-08-18", "SREALITY"),
            Row(6_897_485m, "2026-08-18", "BAZOS"),
            Row(6_897_485m, "2026-09-30", "REALMIX"),
        ]);

        Assert.Equal([7_427_735m, 6_897_485m], merged.Select(m => m.Price!.Value));
        Assert.Equal("SREALITY", merged[1].Source);
    }

    [Fact]
    public void LaggingPortal_WithOldPrice_DoesNotRevertTheSeries()
    {
        var merged = ListingService.MergePriceHistory(
        [
            Row(7_427_735m, "2026-06-13", "SREALITY"),
            Row(6_897_485m, "2026-08-18", "SREALITY"),
            Row(7_427_735m, "2026-09-28", "IDNES"),      // portál se starou cenou
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(6_897_485m, merged[^1].Price);
    }

    [Fact]
    public void SameSource_RaisingPriceBack_IsKept()
    {
        var merged = ListingService.MergePriceHistory(
        [
            Row(5_000_000m, "2026-06-01", "SREALITY"),
            Row(4_800_000m, "2026-07-01", "SREALITY"),
            Row(5_000_000m, "2026-08-01", "SREALITY"),
        ]);

        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void DaysOnMarket_UsesGroupFirstSeen()
    {
        var listing = new RealEstate.Domain.Entities.Listing { IsActive = true, FirstSeenAt = DateTime.UtcNow.AddDays(-51) };

        Assert.Equal(51, ListingService.DaysOnMarket(listing));
        Assert.Equal(110, ListingService.DaysOnMarket(listing, DateTime.UtcNow.AddDays(-110)));
    }
}
