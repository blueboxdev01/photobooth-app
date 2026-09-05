using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Photobooth.Cameras;
using Photobooth.Core;

namespace Photobooth.Cameras.Tests;

/// <summary>
/// One test per way a file can lie about being ready.
///
/// These run against a real folder and a real clock, because the thing under
/// test *is* filesystem timing -- a faked clock would skip past the exact
/// interleaving that causes truncated JPEGs. Timings are configured down so the
/// suite still runs in seconds.
/// </summary>
public sealed class WatchFolderCameraTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pb-tests", Guid.NewGuid().ToString("N"));

    private string WatchDir => Path.Combine(_root, "watch");
    private string SampleDir => Path.Combine(_root, "samples");

    private WatchFolderCamera _camera = null!;
    private MockEosUtility _mock = null!;
    private readonly List<CapturedPhoto> _accepted = [];
    private readonly List<CameraStatusEventArgs> _statuses = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(WatchDir);
        Directory.CreateDirectory(SampleDir);
        WriteSample("sample-1.jpg", 240_000);
        WriteSample("sample-2.jpg", 200_000);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_camera is not null)
        {
            await _camera.DisposeAsync();
        }

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Ingest never decodes an image, so deterministic bytes of a realistic size
    /// exercise the same code paths a real 24 MP JPEG would.
    /// </summary>
    private void WriteSample(string name, int bytes)
    {
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Path.Combine(SampleDir, name), data);
    }

    private void Build(
        int completionTimeoutMs = 1500,
        int maxAttempts = 3,
        int chunkDelayMs = 20,
        int stallSeconds = 25)
    {
        var watch = Options.Create(new WatchFolderOptions
        {
            Path = WatchDir,
            Extensions = WatchFolderOptions.DefaultExtensions,
            StabilityPollMilliseconds = 25,
            StabilityChecks = 2,
            CompletionTimeoutMilliseconds = completionTimeoutMs,
            SweepIntervalMilliseconds = 150,
            MinimumFileSizeBytes = 1024,
            MaxCompletionAttempts = maxAttempts,
        });

        _camera = new WatchFolderCamera(watch, NullLogger<WatchFolderCamera>.Instance);
        _camera.PhotoArrived += (_, e) => { lock (_accepted) { _accepted.Add(e.Photo); } };
        _camera.StatusChanged += (_, e) => { lock (_statuses) { _statuses.Add(e); } };

        _mock = new MockEosUtility(
            Options.Create(new MockEosUtilityOptions
            {
                SourceFolder = SampleDir,
                ChunkBytes = 16 * 1024,
                ChunkDelayMilliseconds = chunkDelayMs,
                StallSeconds = stallSeconds,
            }),
            watch,
            NullLogger<MockEosUtility>.Instance);
    }

    private int AcceptedCount { get { lock (_accepted) { return _accepted.Count; } } }

    private List<CapturedPhoto> Accepted { get { lock (_accepted) { return [.. _accepted]; } } }

    private async Task<bool> WaitForPhotosAsync(int count, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (AcceptedCount >= count) return true;
            await Task.Delay(25);
        }

        return AcceptedCount >= count;
    }

    private static async Task<bool> StaysAtAsync(Func<int> probe, int expected, int forMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(forMs);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() != expected) return false;
            await Task.Delay(25);
        }

        return true;
    }

    [Fact]
    public async Task Accepts_a_photo_once_it_is_completely_written()
    {
        Build();
        await _camera.ConnectAsync();

        var written = await _mock.SimulatePressAsync();

        Assert.True(await WaitForPhotosAsync(1), "no photo was accepted");
        var photo = Accepted.Single();
        Assert.Equal(Path.GetFileName(written), photo.FileName);

        // The whole point: what we accepted is the complete file, not a prefix.
        Assert.Equal(new FileInfo(written).Length, photo.SizeBytes);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(SampleDir, "sample-1.jpg")),
            File.ReadAllBytes(photo.FilePath));
    }

    [Fact]
    public async Task Does_not_accept_a_file_while_it_is_still_being_written()
    {
        // 240 KB in 16 KB chunks at 20 ms each is roughly 300 ms of writing, so a
        // watcher that fired on creation would have ample time to read a prefix.
        Build();
        await _camera.ConnectAsync();

        var press = _mock.SimulatePressAsync();
        var stayedEmpty = await StaysAtAsync(() => AcceptedCount, 0, 150);
        await press;

        Assert.True(stayedEmpty, "a partially written file was accepted");
        Assert.True(await WaitForPhotosAsync(1), "the completed file was never accepted");
    }

    [Fact]
    public async Task Ignores_a_file_written_before_the_session_started()
    {
        Build();
        await _camera.ConnectAsync();
        _camera.AcceptFrom = DateTimeOffset.UtcNow;

        await _mock.SimulatePressAsync(MockWriteMode.Stale);

        Assert.True(await StaysAtAsync(() => AcceptedCount, 0, 1200),
            "a leftover from a previous session leaked into this one");
    }

    [Fact]
    public async Task Accepts_both_photos_from_a_rapid_double_press()
    {
        Build();
        await _camera.ConnectAsync();

        await Task.WhenAll(_mock.SimulatePressAsync(), _mock.SimulatePressAsync());

        Assert.True(await WaitForPhotosAsync(2), $"expected 2, got {AcceptedCount}");
        Assert.Equal(2, Accepted.Select(p => p.FileName).Distinct().Count());
    }

    [Fact]
    public async Task Does_not_accept_the_same_file_twice()
    {
        Build();
        await _camera.ConnectAsync();
        await _mock.SimulatePressAsync();
        Assert.True(await WaitForPhotosAsync(1));

        // Same name again: the file is rewritten, but it is the photo we already
        // put on the strip, and adding it twice would duplicate a frame.
        await _mock.SimulatePressAsync(MockWriteMode.DuplicateName);

        Assert.True(await StaysAtAsync(() => AcceptedCount, 1, 1200),
            "an already-accepted file was accepted a second time");
    }

    [Fact]
    public async Task Ignores_files_that_are_not_photos()
    {
        // RAW sidecars appear if RAW ever gets switched on by accident, and must
        // never be treated as the photo.
        Build();
        await _camera.ConnectAsync();

        File.WriteAllBytes(Path.Combine(WatchDir, "IMG_0001.CR3"), new byte[300_000]);

        Assert.True(await StaysAtAsync(() => AcceptedCount, 0, 1200));
    }

    [Fact]
    public async Task Ignores_files_below_the_minimum_size()
    {
        Build();
        await _camera.ConnectAsync();

        File.WriteAllBytes(Path.Combine(WatchDir, "IMG_0001.JPG"), new byte[64]);

        Assert.True(await StaysAtAsync(() => AcceptedCount, 0, 1200));
    }

    [Fact]
    public async Task Never_accepts_a_stalled_transfer_as_a_truncated_photo()
    {
        Build(completionTimeoutMs: 600, maxAttempts: 99, stallSeconds: 3);
        await _camera.ConnectAsync();

        await _mock.SimulatePressAsync(MockWriteMode.NeverFinishes);

        Assert.True(await StaysAtAsync(() => AcceptedCount, 0, 2000),
            "half a JPEG was accepted as a photo");
    }

    [Fact]
    public async Task Gives_up_on_a_stuck_file_and_reports_the_fault()
    {
        // Without a cap, the periodic sweep would re-offer this file forever and
        // ingest would spend the rest of the event timing out on it.
        Build(completionTimeoutMs: 400, maxAttempts: 2, stallSeconds: 6);
        await _camera.ConnectAsync();

        await _mock.SimulatePressAsync(MockWriteMode.NeverFinishes);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && _camera.AbandonedFiles.Count == 0)
        {
            await Task.Delay(50);
        }

        Assert.Single(_camera.AbandonedFiles);
        Assert.Equal(CameraStatus.Faulted, _camera.Status);

        List<CameraStatusEventArgs> statuses;
        lock (_statuses) { statuses = [.. _statuses]; }
        Assert.Contains(statuses, s =>
            s.Status == CameraStatus.Faulted && s.Message!.Contains("never finished"));
    }

    [Fact]
    public async Task A_healthy_photo_after_a_fault_clears_the_fault()
    {
        Build(completionTimeoutMs: 400, maxAttempts: 1, stallSeconds: 5);
        await _camera.ConnectAsync();
        await _mock.SimulatePressAsync(MockWriteMode.NeverFinishes);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && _camera.Status != CameraStatus.Faulted)
        {
            await Task.Delay(50);
        }

        Assert.Equal(CameraStatus.Faulted, _camera.Status);

        await _mock.SimulatePressAsync();

        Assert.True(await WaitForPhotosAsync(1));
        Assert.Equal(CameraStatus.Ready, _camera.Status);
    }

    [Fact]
    public async Task Finds_a_photo_the_file_watcher_never_reported()
    {
        // FileSystemWatcher drops events under load. The file is written before
        // the watcher exists, so only the periodic sweep can find it.
        Build();
        File.Copy(
            Path.Combine(SampleDir, "sample-1.jpg"),
            Path.Combine(WatchDir, "IMG_0001.JPG"));

        await _camera.ConnectAsync();

        Assert.True(await WaitForPhotosAsync(1),
            "the sweep did not recover a file the watcher missed");
    }
}
