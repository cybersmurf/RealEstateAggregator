using System.Globalization;
using SkiaSharp;

namespace RealEstate.Api.Services.Vision;

public interface ICuzkMapService
{
    /// <summary>Ortofoto ČÚZK 200 × 200 m kolem bodu s červeným zaměřovačem uprostřed; null když služba neodpoví.</summary>
    Task<byte[]?> OrthophotoAsync(double latitude, double longitude, CancellationToken ct);

    /// <summary>Katastrální mapa (hranice parcel, půdorysy budov, parcelní čísla) téhož výřezu se zaměřovačem.</summary>
    Task<byte[]?> CadastralMapAsync(double latitude, double longitude, CancellationToken ct);
}

/// <summary>
/// Veřejné WMS služby ČÚZK. Letecký snímek v galerii inzerátu je náhoda, ortofoto a katastrální mapa
/// kolem GPS jsou k dispozici vždy – obrazový model z nich vidí, zda se půdorys domu dotýká budov na
/// sousedních parcelách, a nemusí hádat z fotky z ulice.
/// </summary>
public sealed class CuzkMapService(IHttpClientFactory httpClientFactory, ILogger<CuzkMapService> logger) : ICuzkMapService
{
    public const double RadiusMeters = 100;
    public const int SizePx = 1024;
    public const string OrthophotoWms = "https://ags.cuzk.gov.cz/arcgis1/services/ORTOFOTO/MapServer/WMSServer";
    public const string CadastreWms = "https://services.cuzk.cz/wms/wms.asp";

    public Task<byte[]?> OrthophotoAsync(double latitude, double longitude, CancellationToken ct)
        => FetchAsync(WmsUrl(OrthophotoWms, "0", "image/jpeg", BoundingBox(latitude, longitude, RadiusMeters)), ct);

    public Task<byte[]?> CadastralMapAsync(double latitude, double longitude, CancellationToken ct)
        => FetchAsync(WmsUrl(CadastreWms, "hranice_parcel,POL_BUDOV,parcelni_cisla", "image/png", BoundingBox(latitude, longitude, RadiusMeters)) + "&TRANSPARENT=FALSE&BGCOLOR=0xFFFFFF", ct);

    // ═══════════════════════════════════════════════════════════════════════════
    // Čistá logika (public static kvůli unit testům)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Čtverec o straně 2 × <paramref name="radiusMeters"/> kolem bodu (WGS84, na 49° s. š. je 1° délky ~73 km).</summary>
    public static (double MinLat, double MinLon, double MaxLat, double MaxLon) BoundingBox(double latitude, double longitude, double radiusMeters)
    {
        var dLat = radiusMeters / 111_320.0;
        var dLon = radiusMeters / (111_320.0 * Math.Cos(latitude * Math.PI / 180));
        return (latitude - dLat, longitude - dLon, latitude + dLat, longitude + dLon);
    }

    public static string WmsUrl(string baseUrl, string layers, string format, (double MinLat, double MinLon, double MaxLat, double MaxLon) bbox)
        => string.Create(CultureInfo.InvariantCulture,
            $"{baseUrl}?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS={layers}&STYLES=&CRS=EPSG:4326&BBOX={bbox.MinLat:F6},{bbox.MinLon:F6},{bbox.MaxLat:F6},{bbox.MaxLon:F6}&WIDTH={SizePx}&HEIGHT={SizePx}&FORMAT={Uri.EscapeDataString(format)}");

    /// <summary>Červený zaměřovač uprostřed – model musí vědět, která z budov je ta prodávaná. Nedekódovatelný obrázek vrátí beze změny.</summary>
    public static byte[] MarkCenter(byte[] image)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(image);
            if (bitmap is null) return image;
            using var canvas = new SKCanvas(bitmap);
            var (cx, cy) = (bitmap.Width / 2f, bitmap.Height / 2f);
            var radius = Math.Max(12f, bitmap.Width / 36f);
            using var paint = new SKPaint { Color = new SKColor(230, 30, 30), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3f, bitmap.Width / 300f) };
            canvas.DrawCircle(cx, cy, radius, paint);
            foreach (var (dx, dy) in new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) })
                canvas.DrawLine(cx + dx * radius * 0.6f, cy + dy * radius * 0.6f, cx + dx * radius * 1.8f, cy + dy * radius * 1.8f, paint);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 88);
            return encoded?.ToArray() ?? image;
        }
        catch (Exception)
        {
            return image;
        }
    }

    private async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; RealEstateAggregator/1.0)");
            using var response = await http.GetAsync(url, ct);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!response.IsSuccessStatusCode || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("ČÚZK WMS {Url}: HTTP {Status}, {Type}", url[..url.IndexOf('?')], (int)response.StatusCode, mediaType);
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length < 1_000 ? null : MarkCenter(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "ČÚZK WMS nedostupné");
            return null;
        }
    }
}
