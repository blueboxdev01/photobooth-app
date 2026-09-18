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

    // --- the animation, added after the fact -------------------------------

    /// <summary>
    /// The compatibility claim. Every session.json already on disk was written
    /// before animations existed, and the booth reads all of them on every
    /// delivery poll -- so a record that cannot be deserialised is not a stale
    /// file, it is the upload queue jamming on a folder it can no longer read.
    /// </summary>
    [Fact]
    public void A_record_written_before_animations_existed_still_loads()
    {
        var record = Save();
        var path = Path.Combine(_archive.FolderFor(record), "session.json");

        // Written by hand with no "animation" key at all, exactly as an older
        // build left it.
        var legacy = System.Text.Json.JsonSerializer.Serialize(new
        {
            token = record.Token,
            folderName = record.FolderName,
            createdUtc = record.CreatedUtc,
            template = record.Template,
            shotCount = record.ShotCount,
            strip = record.Strip,
            photos = record.Photos,
            sourceFiles = record.SourceFiles,
            uploadState = record.UploadState,
        });

        File.WriteAllText(path, legacy);

        var loaded = _archive.All().SingleOrDefault(r => r.FolderName == record.FolderName);

        Assert.NotNull(loaded);
        Assert.Equal(record.Strip, loaded!.Strip);
        Assert.Null(loaded.Animation);
    }

    [Fact]
    public void An_animation_is_filed_beside_the_strip_it_copies()
    {
        Directory.CreateDirectory(_root);
        var photo = Path.Combine(_root, "src.jpg");
        File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);

        var gif = Path.Combine(_root, "src.gif");
        File.WriteAllBytes(gif, [0x47, 0x49, 0x46, 0x38, 0x39, 0x61]);

        var template = new StripTemplate(
            "fake", new TemplateCanvas(600, 1800), [new TemplateSlot(0, 0, 1, 0.3)]);

        var record = _archive.Save(
            "tokGIF", template,
            [new CapturedPhoto(photo, "IMG_0001.JPG", 4, DateTimeOffset.UtcNow)],
            photo, DateTimeOffset.UtcNow, gif);

        Assert.Equal("strip.gif", record.Animation);
        Assert.True(File.Exists(Path.Combine(_archive.FolderFor(record), "strip.gif")));
    }

    /// <summary>
    /// A session where building the animation failed must archive perfectly
    /// happily, because the strip and the photos are the deliverable.
    /// </summary>
    [Fact]
    public void A_session_with_no_animation_archives_normally()
    {
        var record = Save();

        Assert.Null(record.Animation);
        Assert.False(File.Exists(Path.Combine(_archive.FolderFor(record), "strip.gif")));
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

    // --- reading through the cache -----------------------------------------

    /// <summary>
    /// The performance claim, stated so it can fail.
    ///
    /// <para>
    /// A session.json that has not changed is not opened again. Proven by
    /// replacing the file with rubbish while carefully leaving its length and
    /// write time alone: anything that re-reads gets a parse failure and drops
    /// the session, so the good record coming back can only have come from
    /// memory. Nothing does this in reality -- it is a way to observe a thing
    /// whose only other symptom is a stopwatch.
    /// </para>
    /// </summary>
    [Fact]
    public void An_unchanged_session_is_not_read_from_disk_twice()
    {
        var record = Save();
        var path = Path.Combine(_archive.FolderFor(record), "session.json");

        Assert.Equal(record.FolderName, _archive.All().Single().FolderName);

        var length = new FileInfo(path).Length;
        var written = File.GetLastWriteTimeUtc(path);

        File.WriteAllText(path, new string('x', (int)length));
        File.SetLastWriteTimeUtc(path, written);

        Assert.Equal(record.FolderName, _archive.All().Single().FolderName);
    }

    /// <summary>
    /// The archive is a pile of text files on purpose, and the runbook tells the
    /// operator to edit one by hand to release a session that has jammed. A cache
    /// that kept what it read first would make that instruction quietly false --
    /// the operator would fix the file, watch nothing happen, and have no way to
    /// tell why.
    /// </summary>
    [Fact]
    public void A_record_edited_by_hand_is_picked_up()
    {
        var record = Save();
        var path = Path.Combine(_archive.FolderFor(record), "session.json");

        Assert.Equal(UploadStates.NotAttempted, _archive.All().Single().UploadState);

        // As an operator would: the booth is not holding the file, and the edit
        // lands some time after the booth last read it.
        var edited = File.ReadAllText(path)
            .Replace(UploadStates.NotAttempted, UploadStates.Pending);

        File.WriteAllText(path, edited);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(UploadStates.Pending, _archive.All().Single().UploadState);
    }

    /// <summary>
    /// Deleting a session folder is how an operator removes a guest's photos, and it
    /// has to actually remove them rather than leave a remembered copy answering
    /// for them.
    /// </summary>
    [Fact]
    public void A_deleted_session_stops_being_listed()
    {
        var record = Save();

        Assert.Single(_archive.All());

        Directory.Delete(_archive.FolderFor(record), recursive: true);

        Assert.Empty(_archive.All());
    }

    /// <summary>
    /// The booth's own writes are never missed, even when the file gives nothing
    /// away.
    ///
    /// <para>
    /// The upload queue counts attempts, so 1 becomes 2 in a file of exactly the
    /// same length -- and on a FAT32 output folder, which is what a USB stick is,
    /// the write time it is stamped with rounds to two seconds and need not move
    /// either. This reproduces that by putting the timestamp back: from the
    /// outside the file is untouched, and only the writer knowing it wrote can
    /// tell. Without that, a retry counter would stick and the queue would read
    /// its own work as though it had never happened.
    /// </para>
    /// </summary>
    [Fact]
    public void A_rewrite_of_the_same_size_at_the_same_moment_is_still_seen()
    {
        var record = Save();
        var folder = _archive.FolderFor(record);
        var path = Path.Combine(folder, "session.json");

        Assert.Equal(0, _archive.All().Single().UploadAttempts);

        var before = new FileInfo(path);
        var length = before.Length;
        var written = before.LastWriteTimeUtc;

        _archive.WriteRecord(folder, record with { UploadAttempts = 1 });

        // Hold the filesystem still, the way a coarse one would.
        File.SetLastWriteTimeUtc(path, written);

        // The premise, checked rather than assumed: if a future field made these
        // differ, the test would pass for the wrong reason and stop guarding
        // anything.
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        Assert.Equal(1, _archive.All().Single().UploadAttempts);
    }
}
