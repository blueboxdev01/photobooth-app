using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Photobooth.Core;

namespace Photobooth.Cameras;

/// <summary>
/// Ingests photos by watching the folder EOS Utility writes into.
///
/// This adapter never talks to the camera. The camera sits upstream of EOS
/// Utility, which sits upstream of this folder, which is why the whole app can
/// be built and tested with no camera present.
/// </summary>
public sealed class WatchFolderCamera : ICameraDevice
{
    private readonly WatchFolderOptions _options;
    private readonly ILogger<WatchFolderCamera> _logger;
    private readonly TimeProvider _time;

    private readonly Channel<string> _candidates =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    // Dedup: the watcher, the sweep, and Created-then-Renamed can all surface the
    // same file. Accepting it twice would put the same photo on the strip twice.
    private readonly ConcurrentDictionary<string, byte> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    // Queued or currently being examined. Without this, the periodic sweep
    // re-offers a slow file every couple of seconds and the queue fills with
    // duplicate attempts at the same path, each of which can block for the full
    // completion timeout.
    private readonly ConcurrentDictionary<string, byte> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _processor;
    private Task? _sweeper;

    public WatchFolderCamera(
        IOptions<WatchFolderOptions> options,
        ILogger<WatchFolderCamera> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public CameraCapabilities Capabilities => CameraCapabilities.ObserveOnly;

    public CameraStatus Status { get; private set; } = CameraStatus.Disconnected;

    /// <summary>
    /// Files written before this instant are ignored. The session engine moves it
    /// forward when a session starts, so leftovers from a previous session (or
    /// from the operator testing the camera) can never leak onto a strip.
    /// </summary>
    public DateTimeOffset AcceptFrom { get; set; } = DateTimeOffset.MinValue;

    public string WatchFolderPath => System.IO.Path.GetFullPath(_options.Path);

    public event EventHandler<PhotoArrivedEventArgs>? PhotoArrived;
    public event EventHandler<CameraStatusEventArgs>? StatusChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
        {
            return Task.CompletedTask;
        }

        var folder = WatchFolderPath;
        Directory.CreateDirectory(folder);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        // Created and Renamed both matter: some tethering software writes to a
        // temporary name and renames on completion.
        _watcher.Created += (_, e) => Offer(e.FullPath);
        _watcher.Renamed += (_, e) => Offer(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "Watcher error; relying on the periodic sweep.");
            SetStatus(CameraStatus.Faulted, "File watcher error; falling back to polling.");
        };

        _processor = Task.Run(() => ProcessAsync(token), token);
        if (_options.SweepIntervalMilliseconds > 0)
        {
            _sweeper = Task.Run(() => SweepAsync(token), token);
        }

        SetStatus(CameraStatus.Ready, "Watching " + folder);
        _logger.LogInformation("Watching {Folder} for {Extensions}",
            folder, string.Join(", ", _options.Extensions));

        return Task.CompletedTask;
    }

    /// <summary>No-op: the shutter is fired by a physical remote, not by us.</summary>
    public Task RequestCaptureAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>Forget accepted files, so a fresh session starts clean.</summary>
    public void ResetSeen() => _seen.Clear();

    // Known limitation, to settle in M3: candidates are processed one at a time to
    // preserve capture order, so a stalled transfer delays photos queued behind it
    // by up to CompletionTimeoutMilliseconds. The alternative is bounded
    // concurrency plus sorting by write time; deferred until the field test shows
    // whether stalled transfers actually happen.

    private void Offer(string path)
    {
        if (!HasWatchedExtension(path) || _seen.ContainsKey(path))
        {
            return;
        }

        if (!_inFlight.TryAdd(path, 0))
        {
            return;
        }

        if (!_candidates.Writer.TryWrite(path))
        {
            _inFlight.TryRemove(path, out _);
        }
    }

    private bool HasWatchedExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return _options.Extensions.Any(
            e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SweepAsync(CancellationToken token)
    {
        // FileSystemWatcher misses events when many files land at once. Re-scanning
        // costs nothing at this volume and turns a dropped event into a late one
        // rather than a lost photo.
        var delay = TimeSpan.FromMilliseconds(_options.SweepIntervalMilliseconds);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, _time, token).ConfigureAwait(false);
                foreach (var path in Directory.EnumerateFiles(WatchFolderPath))
                {
                    Offer(path);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sweep failed.");
            }
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        try
        {
            await foreach (var path in _candidates.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await TryAcceptAsync(path, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to ingest {Path}", path);
                }
                finally
                {
                    // Released even on failure, so a file that was still being
                    // written gets another chance on the next sweep.
                    _inFlight.TryRemove(path, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task TryAcceptAsync(string path, CancellationToken token)
    {
        if (_seen.ContainsKey(path))
        {
            return;
        }

        var info = await WaitUntilCompleteAsync(path, token).ConfigureAwait(false);
        if (info is null)
        {
            _logger.LogWarning("Ignoring {File}: never finished being written.",
                System.IO.Path.GetFileName(path));
            return;
        }

        if (info.Length < _options.MinimumFileSizeBytes)
        {
            _logger.LogDebug("Ignoring {File}: {Size} bytes is below the minimum.",
                info.Name, info.Length);
            return;
        }

        // Stale-file guard. Uses last-write rather than creation time, because a
        // file copied into the folder keeps its original creation timestamp.
        var written = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (written < AcceptFrom)
        {
            _logger.LogInformation(
                "Ignoring {File}: written {Written}, before this session began {From}.",
                info.Name, written, AcceptFrom);
            return;
        }

        if (!_seen.TryAdd(path, 0))
        {
            return;
        }

        var photo = new CapturedPhoto(info.FullName, info.Name, info.Length, _time.GetUtcNow());
        _logger.LogInformation("Accepted {File} ({Size} KB)", info.Name, info.Length / 1024);
        PhotoArrived?.Invoke(this, new PhotoArrivedEventArgs(photo));
    }

    /// <summary>
    /// Waits until a file has stopped growing and can be opened exclusively.
    ///
    /// FileSystemWatcher fires on creation, not completion, so without this a
    /// 24 MP JPEG gets read while it is still arriving. Returns null if the file
    /// never settles within the timeout.
    /// </summary>
    private async Task<FileInfo?> WaitUntilCompleteAsync(string path, CancellationToken token)
    {
        var deadline = _time.GetUtcNow()
            .AddMilliseconds(_options.CompletionTimeoutMilliseconds);
        var poll = TimeSpan.FromMilliseconds(_options.StabilityPollMilliseconds);

        long lastSize = -1;
        var stableCount = 0;

        while (_time.GetUtcNow() < deadline)
        {
            token.ThrowIfCancellationRequested();

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length == lastSize && info.Length > 0)
            {
                stableCount++;

                // A steady size is necessary but not sufficient: the writer may
                // simply be between chunks. An exclusive open is the real proof
                // that nothing still holds the file open for writing.
                if (stableCount >= _options.StabilityChecks && CanOpenExclusively(path))
                {
                    info.Refresh();
                    return info;
                }
            }
            else
            {
                stableCount = 0;
                lastSize = info.Length;
            }

            await Task.Delay(poll, _time, token).ConfigureAwait(false);
        }

        return null;
    }

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetStatus(CameraStatus status, string? message)
    {
        Status = status;
        StatusChanged?.Invoke(this, new CameraStatusEventArgs(status, message));
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        _candidates.Writer.TryComplete();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (var task in new[] { _processor, _sweeper })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
        _cts = null;
        SetStatus(CameraStatus.Disconnected, null);
    }
}
