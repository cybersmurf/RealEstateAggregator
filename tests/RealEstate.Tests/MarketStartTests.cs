using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Dny na trhu od data vložení u zdroje – rozbor domů 7. 11. 2026: Suchohrdly visí na Sreality
//  od 27. 6. 2025, aplikace ukazovala dny od našeho prvního spatření.
// ─────────────────────────────────────────────────────────────────
public class MarketStartTests
{
    private static readonly DateTime FirstSeen = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DrivejsiDatumZdrojeMaPrednost()
    {
        var created = new DateTimeOffset(2025, 6, 27, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(created.UtcDateTime, ListingService.MarketStart(FirstSeen, created));
    }

    [Fact]
    public void BezDataZdrojePlatiPrvniSpatreni()
    {
        Assert.Equal(FirstSeen, ListingService.MarketStart(FirstSeen, null));
    }

    [Fact]
    public void PozdejsiDatumZdrojeNicNemeni()
    {
        Assert.Equal(FirstSeen, ListingService.MarketStart(FirstSeen, FirstSeen.AddDays(3)));
    }

    [Theory]
    [InlineData(1970)]
    [InlineData(2009)]
    public void NeverohodneStareDatumSeIgnoruje(int year)
    {
        Assert.Equal(FirstSeen, ListingService.MarketStart(FirstSeen, new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
