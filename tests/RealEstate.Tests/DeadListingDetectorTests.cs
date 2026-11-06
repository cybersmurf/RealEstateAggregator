using System.Net;
using RealEstate.Api.Services;

namespace RealEstate.Tests;

// ─────────────────────────────────────────────────────────────────
//  Kontrola mrtvých odkazů – 6. 11. 2026 prošla 772 zatuchlých inzerátů a nedeaktivovala žádný:
//  smazané inzeráty vracely 200 se štítkem nebo přesměrovaly na výpis.
// ─────────────────────────────────────────────────────────────────
public class DeadListingDetectorTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    public void Status404Nebo410JeMrtvy(HttpStatusCode status)
    {
        Assert.True(DeadListingDetector.IsDead(status, "https://x.cz/1234", null, "1234", null));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void NejistyStatusInzeratNechaAktivni(HttpStatusCode status)
    {
        Assert.False(DeadListingDetector.IsDead(status, "https://x.cz/1234", null, "1234", "smazany_inzerat"));
    }

    [Fact]
    public void RealityCechySmazanyInzeratSeStatusem200()
    {
        const string body = """<div class="row smazany_inzerat"><span class="smazany_inzerat_popis">Nemovitost již není v nabídce. Vyberte si z podobných níže...</span></div>""";
        var url = "https://www.realitycechy.cz/nemovitost/prodej/prodej-rodinneho-domu-181-m2-s-bazenem-veverska-bityska/21188870";

        Assert.True(DeadListingDetector.IsDead(HttpStatusCode.OK, url, url, "21188870", body));
    }

    [Fact]
    public void RealityMixInzeratNeniVDatabazi()
    {
        const string body = "<p>Požadovaný inzerát již není v naší databázi. Našli jsme ale <b>10</b> podobných nemovitostí.</p>";
        var url = "https://realitymix.cz/detail/brod-nad-dyji/prodej-rodinneho-domu-64-m-brod-nad-dyji-8282543.html";

        Assert.True(DeadListingDetector.IsDead(HttpStatusCode.OK, url, url, "8282543", body));
    }

    [Fact]
    public void BazosPresmerovaniNaVypisJeMrtvy()
    {
        Assert.True(DeadListingDetector.IsDead(HttpStatusCode.OK,
            "https://reality.bazos.cz/inzerat/224199351/prodej-rodinneho-domu-290-m2-pozemek-4686-m2.php",
            "https://reality.bazos.cz/inzeraty/prodej-rodinneho/",
            "224199351", "<html>výpis</html>"));
    }

    [Fact]
    public void KanonickePresmerovaniSeStejnymIdJeZivy()
    {
        Assert.False(DeadListingDetector.IsDead(HttpStatusCode.OK,
            "https://www.sreality.cz/detail/prodej/dum/rodinny/brno/3856273484",
            "https://www.sreality.cz/detail/prodej/dum/rodinny/brno-chrlice-u-ustavu/3856273484",
            "3856273484", "<html>detail</html>"));
    }

    [Fact]
    public void ProdanoUPodobnychNabidekNestaci()
    {
        const string body = "<h1>Prodej domu</h1><div class=similar>PRODÁNO Rodinný dům Znojmo</div>";

        Assert.False(DeadListingDetector.IsDead(HttpStatusCode.OK, "https://x.cz/d/5555", "https://x.cz/d/5555", "5555", body));
    }
}
