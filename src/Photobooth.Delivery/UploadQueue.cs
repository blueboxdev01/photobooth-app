using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Photobooth.Delivery;

/// <summary>What the console shows about delivery.</summary>
public sealed record DeliveryStatus(
    bool Enabled,
    bool Authorised,
    int Pending,
    int Failed,
    string? LastError,
    DateTimeOffset? LastSuccessUtc);

/// <summary>
/// Publishes finished sessions in the background, and keeps trying.
///
/// The guest never waits on this. A session is composed and written to disk
/// before the queue hears about it, so a venue with no signal costs the guest a
/// QR code and nothing else -- the photos are already safe and the link can be
/// produced later.
///
/// There is no queue data structure. The queue *is* the archive: the work is
/// every session whose <c>session.json</c> says it has not been published yet.
/// That is why it survives being killed mid-upload with no recovery code, and
/// why the operator can unstick one in Notepad.
/// </summary>
public sealed class UploadQueue : BackgroundService
{
    private readonly SessionArchive _archive;
    private readonly IGalleryPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly DriveOptions _options;
    private readonly ILogger<UploadQueue> _logger;

    /// <summary>
    /// When each session may next be tried. Deliberately in memory only: a
    /// restart is a good reason to try again immediately, whereas the attempt
    /// count that decides "give up" belongs on disk so it cannot be reset by one.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _nextAttempt = [];

    private readonly Lock _sync = new();
    private string? _lastError;
    private DateTimeOffset? _lastSuccessUtc;

    public UploadQueue(
        SessionArchive archive,
        IGalleryPublisher publisher,
        IOptions<DriveOptions> options,
        ILogger<UploadQueue> logger,
        TimeProvider? timeProvider = null)
    {
        _archive = archive;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when a session's delivery reaches a conclusion, good or bad, so the
    /// console and the guest screen can be told. The guest screen in particular
    /// is showing a "preparing your link" message until this fires.
    /// </summary>
    public event EventHandler<SessionRecord>? Settled;

    public DeliveryStatus Status()
    {
        var all = _archive.All();
        lock (_sync)
        {
            return new DeliveryStatus(
                _publisher.Enabled,
                _publisher.Authorised,
                all.Count(r => r.UploadState == UploadStates.Pending),
                all.Count(r => r.UploadState == UploadStates.Failed),
                _lastError,
                _lastSuccessUtc);
        }
    }

    /// <summary>
    /// Offer a freshly archived session to the queue.
    ///
    /// Marking it Pending on disk is what puts it in the queue, so this is also
    /// what makes it survive a crash between here and the first attempt. With
    /// delivery switched off the record is left alone as NotAttempted rather than
    /// accumulating a backlog that would surprise someone who turns it on later.
    /// </summary>
    public SessionRecord Enqueue(SessionRecord record)
    {
        if (!_publisher.Enabled)
        {
            return record;
        }

        var pending = record with
        {
            UploadState = UploadStates.Pending,
            UploadAttempts = 0,
            UploadError = null,
        };

        _archive.WriteRecord(_archive.FolderFor(pending), pending);
        return pending;
    }

    /// <summary>
    /// Put a session back in the queue by hand: one that failed, or one captured
    /// while delivery was switched off. Its attempt count starts again.
    /// </summary>
    public SessionRecord? Republish(string folderName)
    {
        var record = _archive.All().FirstOrDefault(r => r.FolderName == folderName);
        if (record is null)
        {
            return null;
        }

        var pending = record with
        {
            UploadState = UploadStates.Pending,
            UploadAttempts = 0,
            UploadError = null,
        };

        lock (_sync)
        {
            _nextAttempt.Remove(folderName);
        }

        _archive.WriteRecord(_archive.FolderFor(pending), pending);
        _logger.LogInformation("{Folder} queued for re-publishing.", folderName);
        return pending;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The queue must never be the reason the app falls over: the
                // booth carries on capturing whatever Google is doing.
                _logger.LogError(ex, "The upload queue pass failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.IdlePollSeconds), _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass over the pending sessions.
    ///
    /// Separate from the loop above, and public, so the retry and failure
    /// behaviour can be tested by calling it -- no timers, no waiting, and no
    /// need for a Google account.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_publisher.Enabled)
        {
            return;
        }

        var now = _time.GetUtcNow();

        foreach (var record in _archive.All().Where(r => r.UploadState == UploadStates.Pending))
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_nextAttempt.TryGetValue(record.FolderName, out var due) && due > now)
                {
                    continue;
                }
            }

            await PublishOneAsync(record, cancellationToken);
        }
    }

    private async Task PublishOneAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        var folder = _archive.FolderFor(record);
        var attempts = record.UploadAttempts + 1;

        // The folder can be renamed or moved out from under us between listing
        // and publishing. There is nowhere to record an outcome -- the record
        // lives in the folder that just vanished -- so say so and move on. It
        // costs one existence check per pass and heals itself if the folder
        // comes back, which is what happens when somebody moves one by mistake.
        if (!Directory.Exists(folder))
        {
            _logger.LogWarning(
                "Skipping {Folder}: its folder is gone from {Path}.",
                record.FolderName, folder);
            return;
        }

        PublishResult result;
        try
        {
            result = await _publisher.PublishAsync(record, folder, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A publisher that throws is treated as a transient failure rather
            // than trusted to have classified itself.
            result = PublishResult.Fail(PublishFailure.Transient, ex.Message);
        }

        if (result.Ok)
        {
            var done = record with
            {
                UploadState = UploadStates.Uploaded,
                DriveFolderId = result.FolderId,
                DriveUrl = result.Url,
                UploadAttempts = attempts,
                UploadError = null,
            };

            _archive.WriteRecord(folder, done);

            lock (_sync)
            {
                _nextAttempt.Remove(record.FolderName);
                _lastError = null;
                _lastSuccessUtc = _time.GetUtcNow();
            }

            _logger.LogInformation("Published {Folder} to {Url}.", record.FolderName, result.Url);
            Settled?.Invoke(this, done);
            return;
        }

        var error = result.Error ?? "Upload failed.";

        if (!result.WorthRetrying || attempts >= _options.MaxAttempts)
        {
            Park(record, attempts, error, result.Failure);
            return;
        }

        // Exponential backoff, so a venue's wifi coming back is picked up quickly
        // but a sustained outage is not hammered.
        var seconds = Math.Min(
            _options.BaseBackoffSeconds * Math.Pow(2, attempts - 1),
            _options.MaxBackoffSeconds);

        var waiting = record with { UploadAttempts = attempts, UploadError = error };
        _archive.WriteRecord(folder, waiting);

        lock (_sync)
        {
            _nextAttempt[record.FolderName] = _time.GetUtcNow().AddSeconds(seconds);
            _lastError = error;
        }

        _logger.LogWarning(
            "Publishing {Folder} failed (attempt {Attempt}/{Max}): {Error}. Retrying in {Seconds}s.",
            record.FolderName, attempts, _options.MaxAttempts, error, (int)seconds);
    }

    /// <summary>Give up on one session, loudly, without touching the others.</summary>
    private void Park(
        SessionRecord record,
        int attempts,
        string error,
        PublishFailure failure = PublishFailure.Permanent)
    {
        var failed = record with
        {
            UploadState = UploadStates.Failed,
            UploadAttempts = attempts,
            UploadError = error,
        };

        _archive.WriteRecord(_archive.FolderFor(record), failed);

        lock (_sync)
        {
            _nextAttempt.Remove(record.FolderName);
            _lastError = error;
        }

        _logger.LogError(
            "Giving up on {Folder} after {Attempts} attempt(s) ({Failure}): {Error}. "
            + "The photos are safe on disk and it can be re-published.",
            record.FolderName, attempts, failure, error);

        Settled?.Invoke(this, failed);
    }
}
