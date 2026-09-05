using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using Photobooth.Cameras;
using Photobooth.Core;

namespace Photobooth.Server;

/// <summary>A measured gap between pressing the remote and the file appearing.</summary>
public sealed record PressLatency(
    DateTimeOffset PressedAtUtc,
    string FileName,
    double Seconds);

/// <summary>
/// What the app knows about itself, for someone testing it in another building.
///
/// A remote tester cannot show you their screen, so the app has to report on
/// itself: what it saw, what it rejected and why, how long the camera took, and
/// exactly which build produced the answer.
/// </summary>
public sealed class DiagnosticsService
{
    private const int MaxEvents = 400;
    private const int MaxLatencies = 60;

    private readonly WatchFolderOptions _watchOptions;
    private readonly MockEosUtilityOptions _mockOptions;
    private readonly SessionSettings _sessionOptions;
    private readonly WatchFolderCamera _camera;

    private readonly ConcurrentQueue<IngestEvent> _events = new();
    private readonly ConcurrentQueue<PressLatency> _latencies = new();
    private readonly Lock _sync = new();

    private DateTimeOffset? _pendingPress;
    private (DateTimeOffset At, bool Writable)? _writableCache;

    public DiagnosticsService(
        IOptions<WatchFolderOptions> watchOptions,
        IOptions<MockEosUtilityOptions> mockOptions,
        IOptions<SessionSettings> sessionOptions,
        WatchFolderCamera camera)
    {
        _watchOptions = watchOptions.Value;
        _mockOptions = mockOptions.Value;
        _sessionOptions = sessionOptions.Value;
        _camera = camera;
    }

    public static string Version { get; } =
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    public IReadOnlyList<IngestEvent> Events => [.. _events];

    public IReadOnlyList<PressLatency> Latencies => [.. _latencies];

    public void Record(IngestEvent e)
    {
        _events.Enqueue(e);
        while (_events.Count > MaxEvents && _events.TryDequeue(out _)) { }

        if (e.Outcome == IngestOutcome.Accepted)
        {
            CompleteLatency(e);
        }
    }

    /// <summary>
    /// The tester taps this as they press the remote. The app cannot know when the
    /// shutter fired -- that is the whole point of an external trigger -- so the
    /// only way to get press-to-file latency is to have a human mark the moment.
    /// </summary>
    public void MarkPress()
    {
        lock (_sync)
        {
            _pendingPress = DateTimeOffset.UtcNow;
        }
    }

    private void CompleteLatency(IngestEvent e)
    {
        DateTimeOffset pressed;
        lock (_sync)
        {
            if (_pendingPress is null)
            {
                return;
            }

            pressed = _pendingPress.Value;
            _pendingPress = null;
        }

        var seconds = (e.AtUtc - pressed).TotalSeconds;
        if (seconds is < 0 or > 120)
        {
            return; // A stale mark from minutes ago tells us nothing.
        }

        _latencies.Enqueue(new PressLatency(pressed, e.FileName, Math.Round(seconds, 2)));
        while (_latencies.Count > MaxLatencies && _latencies.TryDequeue(out _)) { }
    }

    public object Snapshot()
    {
        var folder = _camera.WatchFolderPath;
        var exists = Directory.Exists(folder);
        var files = exists
            ? Directory.EnumerateFiles(folder)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(25)
                .Select(f => new
                {
                    name = f.Name,
                    sizeBytes = f.Length,
                    lastWriteUtc = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                    watched = _watchOptions.Extensions.Contains(
                        f.Extension.ToLowerInvariant()),
                })
                .ToList<object>()
            : [];

        var latencies = Latencies;

        return new
        {
            build = new
            {
                version = Version,
                startedUtc = Process,
                framework = Environment.Version.ToString(),
                machine = Environment.MachineName,
                os = Environment.OSVersion.VersionString,
            },
            camera = new
            {
                status = _camera.Status.ToString(),
                canTrigger = _camera.Capabilities.CanTrigger,
                abandoned = _camera.AbandonedFiles,
            },
            watchFolder = new
            {
                path = folder,
                exists,
                writable = exists && IsWritableCached(folder),
                freeDiskBytes = FreeDiskBytes(folder),
                recentFiles = files,
            },
            // The assumptions a field test is meant to confirm or demolish.
            assumptions = new
            {
                extensions = _watchOptions.Extensions,
                expectedNameFormat = _mockOptions.FileNameFormat,
                stabilityPollMs = _watchOptions.StabilityPollMilliseconds,
                stabilityChecks = _watchOptions.StabilityChecks,
                completionTimeoutMs = _watchOptions.CompletionTimeoutMilliseconds,
                sweepIntervalMs = _watchOptions.SweepIntervalMilliseconds,
                minimumFileSizeBytes = _watchOptions.MinimumFileSizeBytes,
                maxCompletionAttempts = _watchOptions.MaxCompletionAttempts,
                shotCount = _sessionOptions.ShotCount,
                countdownSeconds = _sessionOptions.CountdownSeconds,
                noPhotoTimeoutSeconds = _sessionOptions.NoPhotoTimeoutSeconds,
            },
            latency = new
            {
                samples = latencies,
                count = latencies.Count,
                averageSeconds = latencies.Count == 0
                    ? (double?)null
                    : Math.Round(latencies.Average(l => l.Seconds), 2),
                maxSeconds = latencies.Count == 0
                    ? (double?)null
                    : latencies.Max(l => l.Seconds),
            },
            ingest = Events.Reverse().Take(120),
        };
    }

    private static readonly DateTimeOffset Process = DateTimeOffset.UtcNow;

    /// <summary>The probe touches the disk, and the page polls; once a minute is plenty.</summary>
    private bool IsWritableCached(string folder)
    {
        if (_writableCache is { } cached
            && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1))
        {
            return cached.Writable;
        }

        var writable = IsWritable(folder);
        _writableCache = (DateTimeOffset.UtcNow, writable);
        return writable;
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long? FreeDiskBytes(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!)
                .AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}
