using Photobooth.Imaging;

namespace Photobooth.Imaging.Tests;

public class QrRendererTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const string Url = "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOpQrStUvWxYz";

    [Fact]
    public void Renders_a_png()
    {
        var png = QrRenderer.Png(Url);

        Assert.True(png.Length > 100);
        Assert.Equal(PngMagic, png.Take(PngMagic.Length));
    }

    /// <summary>
    /// Two guests' links must never produce the same code, which is the only way
    /// a QR could hand someone another guest's photos.
    /// </summary>
    [Fact]
    public void Different_links_give_different_codes()
    {
        var a = QrRenderer.Png(Url + "aaa");
        var b = QrRenderer.Png(Url + "bbb");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_same_link_always_gives_the_same_code()
    {
        Assert.Equal(QrRenderer.Png(Url), QrRenderer.Png(Url));
    }

    /// <summary>
    /// Bigger modules mean more pixels. Guards the size argument actually being
    /// used -- a QR rendered too small to scan across a booth is useless.
    /// </summary>
    [Fact]
    public void A_larger_module_size_gives_a_larger_image()
    {
        Assert.True(QrRenderer.Png(Url, 20).Length > QrRenderer.Png(Url, 4).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_link_is_refused(string url)
    {
        // Rendering a QR for nothing would put a scannable code on the guest
        // screen that leads nowhere, which is worse than showing no code at all.
        Assert.Throws<ArgumentException>(() => QrRenderer.Png(url));
    }

    [Fact]
    public void A_null_link_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => QrRenderer.Png(null!));
    }
}
