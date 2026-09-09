using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Delivery;

namespace Photobooth.Delivery.Tests;

/// <summary>
/// The QR written beside a guest's photos.
///
/// This is the copy that matters after the event: the one on the guest screen is
/// gone the moment the next guest steps in, so a guest who lost their link is
/// found from this file.
/// </summary>
public sealed class QrFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"pb-qr-{Guid.NewGuid():N}");

    private const string Url = "https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp";

    public QrFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void It_writes_a_png_named_for_what_it_is()
    {
        var name = DrivePublisher.WriteQr(_folder, Url, NullLogger.Instance);

        Assert.Equal("qr.png", name);

        var bytes = File.ReadAllBytes(Path.Combine(_folder, name!));
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            bytes.Take(4).ToArray());
    }

    /// <summary>The file has to encode that guest's link, not just be a QR.</summary>
    [Fact]
    public void The_file_encodes_the_link_it_was_given()
    {
        DrivePublisher.WriteQr(_folder, Url, NullLogger.Instance);
        var written = File.ReadAllBytes(Path.Combine(_folder, "qr.png"));

        Assert.Equal(QrRenderer.Png(Url), written);
        Assert.NotEqual(QrRenderer.Png(Url + "x"), written);
    }

    /// <summary>
    /// A convenience image failing must never fail the session. The photos are
    /// uploaded and the guest has their link; losing the spare copy is a warning,
    /// not a reason to report the upload as broken and retry it.
    /// </summary>
    [Fact]
    public void A_folder_that_is_gone_gives_null_rather_than_throwing()
    {
        var missing = Path.Combine(_folder, "not-there");

        var name = DrivePublisher.WriteQr(missing, Url, NullLogger.Instance);

        Assert.Null(name);
    }

    [Fact]
    public void Rewriting_it_replaces_the_old_one()
    {
        DrivePublisher.WriteQr(_folder, Url, NullLogger.Instance);
        DrivePublisher.WriteQr(_folder, Url + "/other", NullLogger.Instance);

        Assert.Single(Directory.EnumerateFiles(_folder, "qr.png"));
        Assert.Equal(
            QrRenderer.Png(Url + "/other"),
            File.ReadAllBytes(Path.Combine(_folder, "qr.png")));
    }
}
