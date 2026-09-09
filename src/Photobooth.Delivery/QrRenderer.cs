using QRCoder;

namespace Photobooth.Delivery;

/// <summary>
/// Turns a session's link into a QR code the guest screen shows.
///
/// Rendered server-side as a PNG rather than drawn in the browser: the guest
/// display is the one surface that absolutely must work, and a code produced by
/// the same process that knows the URL cannot drift from it or fail to load.
/// </summary>
public static class QrRenderer
{
    /// <summary>
    /// A QR PNG for <paramref name="url"/>.
    /// </summary>
    /// <param name="pixelsPerModule">
    /// Size of each square. 20 gives roughly a 600px image for a Drive folder
    /// URL, which is comfortably scannable across a booth from a phone.
    /// </param>
    public static byte[] Png(string url, int pixelsPerModule = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Quartile correction: a booth QR gets photographed at an angle, in bad
        // light, sometimes through a phone case. The extra redundancy costs a
        // slightly denser code and buys a scan that works first time.
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);

        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
