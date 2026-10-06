using RealEstate.Api.Services.Vision;
using SkiaSharp;

namespace RealEstate.Tests;

// Fotky z prohlídky mají 3–6 MB – před odesláním obrazovému modelu se zmenšují.
public class ImageDownscalerTests
{
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.SteelBlue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    [Theory]
    [InlineData(4032, 3024, 1152, 864)]   // fotka z telefonu na šířku
    [InlineData(3024, 4032, 864, 1152)]   // na výšku
    [InlineData(800, 600, 800, 600)]      // malá se nezvětšuje
    public void ToJpeg_LimitsLongerSide(int width, int height, int expectedWidth, int expectedHeight)
    {
        var result = ImageDownscaler.ToJpeg(Jpeg(width, height));

        using var decoded = SKBitmap.Decode(result);
        Assert.Equal((expectedWidth, expectedHeight), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void ToJpeg_NotAnImage_ReturnsInputUnchanged()
    {
        byte[] garbage = [1, 2, 3, 4, 5];

        Assert.Same(garbage, ImageDownscaler.ToJpeg(garbage));
    }
}
