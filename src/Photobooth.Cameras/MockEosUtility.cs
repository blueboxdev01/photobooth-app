using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Photobooth.Cameras;

/// <summary>How the next simulated write should misbehave.</summary>
public enum MockWriteMode
{
    /// <summary>A normal photo: written in chunks, like a real transfer.</summary>
    Normal,

    /// <summary>Reuse the previous file name, as a double-press might.</summary>
    DuplicateName,

    /// <summary>Back-date the file so it looks like a leftover from earlier.</summary>
    Stale,

    /// <summary>
    /// Write half the file and hold the handle open past the ingest timeout, so
    /// the file never becomes readable. Reproduces a transfer that stalls.
    /// </summary>
    NeverFinishes,
}

public sealed class MockEosUtilityOptions
{
    public const string SectionName = "Camera:Mock";

    /// <summary>Folder of sample JPEGs to copy from.</summary>
    public string SourceFolder { get; set; } = "samples";

    /// <summary>EOS Utility's naming convention, as far as we know it.</summary>
    public string FileNameFormat { get; set; } = "IMG_{0:0000}.JPG";

    public int StartIndex { get; set; } = 1;

    /// <summary>Bytes per chunk. Small enough that a half-written file is easy to hit.</summary>
    public int ChunkBytes { get; set; } = 64 * 1024;

    /// <summary>Pause between chunks, imitating transfer over a USB 2.0 link.</summary>
    public int ChunkDelayMilliseconds { get; set; } = 25;
}

/// <summary>
/// Stands in for EOS Utility so the whole app can be exercised with no camera.
///
/// This mock is deliberately adversarial. A mock that wrote files atomically on a
/// tidy schedule would let every ingest bug survive until the camera arrives,
/// which is the opposite of the point: it writes slowly in chunks so the
/// half-written-file problem genuinely occurs, and it can reproduce a duplicate
/// name, a stale file, and a write that never finishes.
/// </summary>
public sealed class MockEosUtility
{
    private readonly MockEosUtilityOptions _options;
    private readonly WatchFolderOptions _watchOptions;
    private readonly ILogger<MockEosUtility> _logger;

    /// <summary>Longer than any sane ingest completion timeout.</summary>
    private static readonly TimeSpan StallDuration = TimeSpan.FromSeconds(25);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextIndex;
    private int _sourceCursor;
    private string? _lastFileName;

    public MockEosUtility(
        IOptions<MockEosUtilityOptions> options,
        IOptions<WatchFolderOptions> watchOptions,
        ILogger<MockEosUtility> logger)
    {
        _options = options.Value;
        _watchOptions = watchOptions.Value;
        _logger = logger;
        _nextIndex = _options.StartIndex;
    }

    public string SourceFolderPath => Path.GetFullPath(_options.SourceFolder);

    public string TargetFolderPath => Path.GetFullPath(_watchOptions.Path);

    /// <summary>Sample images available to copy, in a stable order.</summary>
    public IReadOnlyList<string> SourceImages()
    {
        if (!Directory.Exists(SourceFolderPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(SourceFolderPath)
            .Where(f => _watchOptions.Extensions.Any(
                e => string.Equals(e, Path.GetExtension(f), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Simulate one press of the shutter release: a new JPEG appears in the watch
    /// folder, written the slow way.
    /// </summary>
    public async Task<string> SimulatePressAsync(
        MockWriteMode mode = MockWriteMode.Normal,
        CancellationToken cancellationToken = default)
    {
        var sources = SourceImages();
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"No sample images in {SourceFolderPath}. Add some JPEGs, or point " +
                $"{MockEosUtilityOptions.SectionName}:SourceFolder somewhere that has them.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = sources[_sourceCursor % sources.Count];
            _sourceCursor++;

            var fileName = mode == MockWriteMode.DuplicateName && _lastFileName is not null
                ? _lastFileName
                : string.Format(_options.FileNameFormat, _nextIndex++);

            Directory.CreateDirectory(TargetFolderPath);
            var target = Path.Combine(TargetFolderPath, fileName);

            if (mode == MockWriteMode.NeverFinishes)
            {
                // Deliberately not awaited: the point is a file that is still being
                // written when the caller returns, and stays that way.
                _ = Task.Run(() => StallAsync(source, target), CancellationToken.None);
            }
            else
            {
                await WriteInChunksAsync(source, target, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (mode == MockWriteMode.Stale)
            {
                // Back-date it well before any plausible session start.
                var stale = DateTime.UtcNow.AddHours(-2);
                File.SetLastWriteTimeUtc(target, stale);
                File.SetCreationTimeUtc(target, stale);
            }

            _lastFileName = fileName;
            _logger.LogInformation("Mock wrote {File} ({Mode}) from {Source}",
                fileName, mode, Path.GetFileName(source));
            return target;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Writes half the file, then holds the handle open long enough that ingest
    /// must give up on it, before cleaning up after itself.
    /// </summary>
    private async Task StallAsync(string source, string target)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
            await using (var stream = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                await Task.Delay(StallDuration).ConfigureAwait(false);
            }

            File.Delete(target);
            _logger.LogInformation("Mock abandoned {File} after stalling.",
                Path.GetFileName(target));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stalled write failed for {File}.", target);
        }
    }

    private async Task WriteInChunksAsync(
        string source, string target, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);

        // FileShare.Read mirrors a real writer: a naive reader can open it and get
        // a partial image, which is exactly the failure we are guarding against.
        await using var stream = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.Read);

        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = Math.Min(_options.ChunkBytes, bytes.Length - offset);
            await stream.WriteAsync(bytes.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            offset += count;

            if (_options.ChunkDelayMilliseconds > 0 && offset < bytes.Length)
            {
                await Task.Delay(_options.ChunkDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
