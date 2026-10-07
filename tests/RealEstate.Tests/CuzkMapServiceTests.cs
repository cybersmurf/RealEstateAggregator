using RealEstate.Api.Services;
using RealEstate.Api.Services.Vision;

namespace RealEstate.Tests;

// Mapy ČÚZK kolem GPS domu: výřez, adresa WMS a zaměřovač.
public class CuzkMapServiceTests
{
    [Fact]
    public void BoundingBox_100m_AtLatitude49()
    {
        var (minLat, minLon, maxLat, maxLon) = CuzkMapService.BoundingBox(48.8728, 16.2218, 100);

        Assert.Equal(0.001797, maxLat - minLat, 5);
        Assert.Equal(0.002731, maxLon - minLon, 5);
        Assert.Equal(48.8728, (minLat + maxLat) / 2, 6);
        Assert.Equal(16.2218, (minLon + maxLon) / 2, 6);
    }

    [Fact]
    public void WmsUrl_UsesInvariantDecimalPointAndLatLonOrder()
    {
        var url = CuzkMapService.WmsUrl(CuzkMapService.OrthophotoWms, "0", "image/jpeg", (48.8719, 16.2205, 48.8738, 16.2232));

        Assert.Contains("BBOX=48.871900,16.220500,48.873800,16.223200", url);
        Assert.Contains("CRS=EPSG:4326", url);
        Assert.Contains("FORMAT=image%2Fjpeg", url);
        Assert.DoesNotContain(",0", url.Replace("BBOX=48.871900,16.220500,48.873800,16.223200", ""));
    }

    [Fact]
    public void MarkCenter_UndecodableBytes_ReturnedUnchanged()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        Assert.Same(bytes, CuzkMapService.MarkCenter(bytes));
    }

    [Fact]
    public void BuildPrompt_NumbersEvidenceInOrder()
    {
        var prompt = HousePositionService.BuildPrompt(hasCadastre: true, hasOrthophoto: true, galleryCount: 3);

        Assert.Contains("These 5 images (numbered 1..5)", prompt);
        Assert.Contains("Image 1 is the cadastral map", prompt);
        Assert.Contains("Image 2 is the aerial orthophoto", prompt);
        Assert.Contains("Images 3..5 are photos from the listing", prompt);
        Assert.DoesNotContain("{INTRO}", prompt);
    }

    [Fact]
    public void BuildPrompt_GalleryOnly()
    {
        var prompt = HousePositionService.BuildPrompt(hasCadastre: false, hasOrthophoto: false, galleryCount: 1);

        Assert.Contains("These 1 images (numbered 1..1)", prompt);
        Assert.Contains("Image 1 is a photo from the listing", prompt);
    }
}
