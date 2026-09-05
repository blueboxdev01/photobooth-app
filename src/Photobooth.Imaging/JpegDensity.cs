namespace Photobooth.Imaging;

/// <summary>
/// Writes the physical resolution into a JPEG's JFIF header.
///
/// Skia encodes JPEGs with no density information, which leaves 600x1800 as just
/// pixels. Print dialogs and photo software then guess at the physical size, and
/// a 2x6 strip comes out as whatever the guess was. Stamping 300 DPI makes the
/// file say what it is.
///
/// The edit is deliberately conservative: it only patches a JFIF APP0 segment
/// that is exactly where the standard puts it, and otherwise leaves the file
/// alone rather than risk corrupting a guest's photo.
/// </summary>
public static class JpegDensity
{
    // SOI, then APP0: FF D8 | FF E0 | len(2) | "JFIF\0" | version(2) | units | Xdensity(2) | Ydensity(2)
    private const int App0Marker = 2;
    private const int JfifIdentifier = 6;
    private const int UnitsOffset = 13;
    private const int XDensityOffset = 14;
    private const int YDensityOffset = 16;
    private const int MinimumLength = 18;

    private const byte UnitsDotsPerInch = 1;

    private static ReadOnlySpan<byte> Jfif => "JFIF\0"u8;

    /// <returns>True if the header was stamped; false if the file was left untouched.</returns>
    public static bool Stamp(string path, int dpi)
    {
        if (dpi <= 0 || dpi > ushort.MaxValue)
        {
            return false;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        if (file.Length < MinimumLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[MinimumLength];
        file.ReadExactly(header);

        var looksRight =
            header[0] == 0xFF && header[1] == 0xD8 &&
            header[App0Marker] == 0xFF && header[App0Marker + 1] == 0xE0 &&
            header.Slice(JfifIdentifier, Jfif.Length).SequenceEqual(Jfif);

        if (!looksRight)
        {
            return false;
        }

        header[UnitsOffset] = UnitsDotsPerInch;
        WriteBigEndian(header, XDensityOffset, (ushort)dpi);
        WriteBigEndian(header, YDensityOffset, (ushort)dpi);

        file.Position = 0;
        file.Write(header);
        return true;
    }

    /// <summary>Reads back the stamped density, or null if the file has none.</summary>
    public static (int X, int Y)? Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (file.Length < MinimumLength)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[MinimumLength];
        file.ReadExactly(header);

        var looksRight =
            header[0] == 0xFF && header[1] == 0xD8 &&
            header[App0Marker] == 0xFF && header[App0Marker + 1] == 0xE0 &&
            header.Slice(JfifIdentifier, Jfif.Length).SequenceEqual(Jfif) &&
            header[UnitsOffset] == UnitsDotsPerInch;

        return looksRight
            ? (ReadBigEndian(header, XDensityOffset), ReadBigEndian(header, YDensityOffset))
            : null;
    }

    private static void WriteBigEndian(Span<byte> buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> buffer, int offset) =>
        (buffer[offset] << 8) | buffer[offset + 1];
}
