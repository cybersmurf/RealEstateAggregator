using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace RealEstate.Api.Services.Photos;

/// <summary>
/// Datum pořízení z EXIF (DateTimeOriginal, případně DateTime) v JPEGu. Jen čtení dvou značek –
/// SkiaSharp z EXIF vrací pouze otočení. Čas je v EXIF bez pásma; bereme ho jako Europe/Prague.
/// </summary>
public static class ExifReader
{
    private const ushort TagDateTime = 0x0132;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagDateTimeOriginal = 0x9003;

    public static DateTime? TakenAtUtc(ReadOnlySpan<byte> jpeg)
    {
        var local = TakenAt(jpeg);
        if (local is null) return null;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified), zone);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTime.SpecifyKind(local.Value, DateTimeKind.Utc);
        }
    }

    /// <summary>Datum tak, jak je v souboru (bez pásma); null bez EXIF nebo při poškozených datech.</summary>
    public static DateTime? TakenAt(ReadOnlySpan<byte> jpeg)
    {
        var tiff = FindExifTiff(jpeg);
        if (tiff.Length < 8) return null;

        var bigEndian = tiff[0] == 'M' && tiff[1] == 'M';
        if (!bigEndian && !(tiff[0] == 'I' && tiff[1] == 'I')) return null;
        if (ReadU16(tiff, 2, bigEndian) != 0x2A) return null;

        var ifd0 = (int)ReadU32(tiff, 4, bigEndian);
        DateTime? fromIfd0 = null;
        var exifIfd = 0;
        foreach (var (tag, type, count, valueOffset) in Entries(tiff, ifd0, bigEndian))
        {
            if (tag == TagExifIfd && type == 4) exifIfd = (int)valueOffset;
            else if (tag == TagDateTime) fromIfd0 = ReadAscii(tiff, type, count, valueOffset, bigEndian);
        }
        if (exifIfd > 0)
        {
            foreach (var (tag, type, count, valueOffset) in Entries(tiff, exifIfd, bigEndian))
                if (tag == TagDateTimeOriginal && ReadAscii(tiff, type, count, valueOffset, bigEndian) is { } original)
                    return original;
        }
        return fromIfd0;
    }

    private static ReadOnlySpan<byte> FindExifTiff(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return default;
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9) break;           // start of scan / end: EXIF už nebude
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > jpeg.Length) break;
            if (marker == 0xE1 && length >= 8 && jpeg.Slice(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                return jpeg.Slice(pos + 10, length - 8);
            pos += 2 + length;
        }
        return default;
    }

    private static List<(ushort Tag, ushort Type, uint Count, uint ValueOffset)> Entries(ReadOnlySpan<byte> tiff, int ifdOffset, bool bigEndian)
    {
        var list = new List<(ushort, ushort, uint, uint)>();
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length) return list;
        int count = ReadU16(tiff, ifdOffset, bigEndian);
        if (count > 500) return list;
        for (var i = 0; i < count; i++)
        {
            var entry = ifdOffset + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            list.Add((ReadU16(tiff, entry, bigEndian), ReadU16(tiff, entry + 2, bigEndian),
                      ReadU32(tiff, entry + 4, bigEndian), ReadU32(tiff, entry + 8, bigEndian)));
        }
        return list;
    }

    private static DateTime? ReadAscii(ReadOnlySpan<byte> tiff, ushort type, uint count, uint valueOffset, bool bigEndian)
    {
        if (type != 2 || count < 19 || count > 64) return null;
        var offset = (int)valueOffset;
        if (offset < 0 || offset + 19 > tiff.Length) return null;
        var text = Encoding.ASCII.GetString(tiff.Slice(offset, 19));
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> s, int offset, bool bigEndian)
        => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset, 4));
}
