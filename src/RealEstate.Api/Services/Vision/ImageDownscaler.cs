using SkiaSharp;

namespace RealEstate.Api.Services.Vision;

/// <summary>
/// Zmenší fotku před odesláním obrazovému modelu. Fotky z prohlídky mají 3–6 MB a při porovnání
/// jich jde do jednoho dotazu až deset – model víc než ~1 000 px stejně nevyužije.
/// </summary>
public static class ImageDownscaler
{
    public const int DefaultMaxSide = 1152;
    private const int JpegQuality = 80;

    /// <summary>
    /// JPEG s delší stranou nejvýš <paramref name="maxSide"/> px, otočený podle EXIF (telefon fotí na výšku
    /// jako ležatý snímek s příznakem otočení). Obrázek, který nejde dekódovat, vrátí beze změny.
    /// </summary>
    public static byte[] ToJpeg(byte[] image, int maxSide = DefaultMaxSide)
    {
        try
        {
            using var data = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(data);
            if (codec is null) return image;

            using var decoded = SKBitmap.Decode(codec);
            if (decoded is null) return image;

            using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);
            var scale = Math.Min(1.0, maxSide / (double)Math.Max(upright.Width, upright.Height));
            var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
            var height = Math.Max(1, (int)Math.Round(upright.Height * scale));

            using var resized = scale < 1.0
                ? upright.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                : upright.Copy();
            if (resized is null) return image;

            using var encoded = resized.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return encoded?.ToArray() ?? image;
        }
        catch (Exception)
        {
            return image;
        }
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var degrees = origin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (degrees == 0) return source.Copy();

        var swap = degrees is 90 or 270;
        var rotated = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(rotated);
        canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }
}
