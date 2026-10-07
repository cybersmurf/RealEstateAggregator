using System.Text;
using RealEstate.Api.Services.Photos;

namespace RealEstate.Tests;

// Datum pořízení z EXIF – fotky z prohlídky dostávaly čas nahrání místo času focení.
public class ExifReaderTests
{
    /// <summary>Minimální JPEG s APP1/EXIF: IFD0 s DateTime (a odkazem na Exif IFD s DateTimeOriginal).</summary>
    private static byte[] Jpeg(string? dateTime, string? dateTimeOriginal, bool bigEndian = true)
    {
        var tiff = new List<byte>();
        void U16(ushort v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian == bigEndian) Array.Reverse(b); tiff.AddRange(b); }
        void U32(uint v) { var b = BitConverter.GetBytes(v); if (BitConverter.IsLittleEndian == bigEndian) Array.Reverse(b); tiff.AddRange(b); }

        tiff.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(0x2A); U32(8);

        var ifd0Entries = (dateTime is null ? 0 : 1) + (dateTimeOriginal is null ? 0 : 1);
        var ifd0End = 8 + 2 + ifd0Entries * 12 + 4;
        var dateOffset = (uint)ifd0End;
        var exifIfdOffset = (uint)(ifd0End + (dateTime is null ? 0 : 20));
        var originalOffset = exifIfdOffset + 2 + 12 + 4;

        U16((ushort)ifd0Entries);
        if (dateTime is not null) { U16(0x0132); U16(2); U32(20); U32(dateOffset); }
        if (dateTimeOriginal is not null) { U16(0x8769); U16(4); U32(1); U32(exifIfdOffset); }
        U32(0);
        if (dateTime is not null) { tiff.AddRange(Encoding.ASCII.GetBytes(dateTime)); tiff.Add(0); }
        if (dateTimeOriginal is not null)
        {
            U16(1); U16(0x9003); U16(2); U32(20); U32(originalOffset); U32(0);
            tiff.AddRange(Encoding.ASCII.GetBytes(dateTimeOriginal)); tiff.Add(0);
        }

        var app1 = new List<byte>();
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);
        var length = (ushort)(app1.Count + 2);
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) };
        jpeg.AddRange(app1);
        jpeg.AddRange([0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    [Fact]
    public void TakenAt_PrefersDateTimeOriginal()
        => Assert.Equal(new DateTime(2026, 10, 3, 14, 5, 9), ExifReader.TakenAt(Jpeg("2026:10:06 09:00:00", "2026:10:03 14:05:09")));

    [Fact]
    public void TakenAt_FallsBackToIfd0DateTime()
        => Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), ExifReader.TakenAt(Jpeg("2026:10:06 09:00:00", null)));

    [Fact]
    public void TakenAt_LittleEndianTiff()
        => Assert.Equal(new DateTime(2026, 10, 3, 14, 5, 9), ExifReader.TakenAt(Jpeg(null, "2026:10:03 14:05:09", bigEndian: false)));

    [Fact]
    public void TakenAt_NoExif_IsNull()
    {
        Assert.Null(ExifReader.TakenAt([0xFF, 0xD8, 0xFF, 0xD9]));
        Assert.Null(ExifReader.TakenAt(Encoding.ASCII.GetBytes("not a jpeg at all")));
        Assert.Null(ExifReader.TakenAt([]));
    }

    [Fact]
    public void TakenAt_TruncatedExif_IsNull()
    {
        var full = Jpeg(null, "2026:10:03 14:05:09");
        Assert.Null(ExifReader.TakenAt(full.AsSpan(0, full.Length - 12)));
    }

    [Fact]
    public void TakenAtUtc_ConvertsPragueSummerTime()
        => Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), ExifReader.TakenAtUtc(Jpeg(null, "2026:07:01 14:00:00")));
}
