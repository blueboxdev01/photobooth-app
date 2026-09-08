using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Photobooth.Core;
using Photobooth.Delivery;

namespace Photobooth.Delivery.Tests;

/// <summary>
/// Reading and writing a session record while something else is doing the same.
///
/// Not hypothetical: both screens poll the delivery status every few seconds and
/// that reads every session.json, so an upload finishing while anyone has the
/// console open is the normal case, not the edge case.
/// </summary>
public sealed class SessionArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"pb-archive-{Guid.NewGuid():N}");

    private readonly SessionArchive _archive;

    public SessionArchiveTests() =>
        _archive = new SessionArchive(
            Options.Create(new ArchiveOptions { Folder = _root }),
            NullLogger<SessionArchive>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SessionRecord Save()
    {
        Directory.CreateDirectory(_root);
        var photo = Path.Combine(_root, "src.jpg");
        File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);

        var template = new StripTemplate(
            "fake", new TemplateCanvas(600, 1800), [new TemplateSlot(0, 0, 1, 0.3)]);

        return _archive.Save(
            "tok123", template,
            [new CapturedPhoto(photo, "IMG_0001.JPG", 4, DateTimeOffset.UtcNow)],
            photo, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The one that was failing an upload for no reason: a write that collides
    /// with a reader used to throw a sharing violation, and the queue read that
    /// as the upload having failed.
    /// </summary>
    [Fact]
    public async Task Writing_a_record_while_it_is_being_read_does_not_throw()
    {
        var record = Save();
        var folder = _archive.FolderFor(record);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Hammer it from both sides, as the console polling and an upload
        // finishing would.
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _archive.All();
            }
        }));

        var writes = 0;
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _archive.WriteRecord(folder, record with { UploadAttempts = ++writes });
            }
        });

        // No exception from either side is the assertion.
        await Task.WhenAll([writer, .. readers]);

        Assert.True(writes > 0, "the writer never ran");
        Assert.Equal(UploadStates.NotAttempted, _archive.All().Single().UploadState);
    }

    /// <summary>A reader must never catch the file mid-write and skip the session.</summary>
    [Fact]
    public async Task A_reader_never_sees_a_half_written_record()
    {
        var record = Save();
        var folder = _archive.FolderFor(record);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                _archive.WriteRecord(
                    folder,
                    record with { UploadError = new string('x', 500 + (n++ % 400)) });
            }
        });

        var reads = 0;
        var lost = 0;
        while (!stop.IsCancellationRequested)
        {
            reads++;
            if (_archive.All().Count != 1)
            {
                lost++;
            }
        }

        await writer;

        Assert.True(reads > 10, $"only managed {reads} reads");
        Assert.Equal(0, lost);
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        var record = Save();
        var folder = _archive.FolderFor(record);

        _archive.WriteRecord(folder, record with { UploadState = UploadStates.Uploaded });

        Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp"));
        Assert.Equal(UploadStates.Uploaded, _archive.All().Single().UploadState);
    }
}
