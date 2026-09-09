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

    /// <summary>
    /// Poked when work arrives, so a finished session is picked up now rather
    /// than whenever the idle poll next comes round. Without it the booth
    /// composes a strip and then sits doing nothing for up to
    /// <see cref="DriveOptions.IdlePollSeconds"/> -- which measured as fourteen
    /// of the seventeen seconds a guest spent waiting for their QR code.
    /// </summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

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
    /// Raised whenever a session's delivery record changes -- including the
    /// moment its link becomes usable, part-way through the upload -- so the
    /// console and the guest screen can be told. The guest screen shows
    /// "preparing your link" until this brings it one with a URL.
    /// </summary>
    public event EventHandler<SessionRecord>? Updated;

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
        Wake();
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
        Wake();
        return pending;
    }

    /// <summary>
    /// Ask the loop to run a pass now. Safe to call repeatedly: the semaphore is
    /// capped at one, so ten sessions finishing together mean one extra pass
    /// rather than ten queued wake-ups.
    /// </summary>
    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pass is already pending, which is all this needs to guarantee.
        }
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

            // Whichever comes first: something to do, or the idle poll coming
            // round. The poll is the safety net for retries whose backoff has
            // expired; new work does not wait for it.
            //
            // The timeout overload rather than racing two tasks with WhenAny:
            // WhenAny leaves the loser running, so every poll that expired left
            // an abandoned WaitAsync queued on the semaphore, and it -- not the
            // next iteration -- collected the following Release. The first
            // session was picked up instantly and every one after it waited out
            // the poll anyway, which is exactly what the logs showed.
            try
            {
                await _wake.WaitAsync(
                    TimeSpan.FromSeconds(_options.IdlePollSeconds), stoppingToken);
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

        // The link becomes usable as soon as the folder and the strip exist,
        // which is well before the raw photos finish. Writing it down and
        // announcing it there and then is what puts the QR in front of the guest
        // while they are still standing at the booth.
        void LinkReady(string folderId, string url)
        {
            var withLink = record with { DriveFolderId = folderId, DriveUrl = url };
            _archive.WriteRecord(folder, withLink);
            _logger.LogInformation(
                "{Folder} is reachable at {Url}; the photos are still uploading.",
                record.FolderName, url);
            Updated?.Invoke(this, withLink);
        }

        PublishResult result;
        try
        {
            result = await _publisher.PublishAsync(record, folder, LinkReady, cancellationToken);
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
                Qr = result.Qr ?? record.Qr,
            };

            _archive.WriteRecord(folder, done);

            lock (_sync)
            {
                _nextAttempt.Remove(record.FolderName);
                _lastError = null;
                _lastSuccessUtc = _time.GetUtcNow();
            }

            _logger.LogInformation("Published {Folder} to {Url}.", record.FolderName, result.Url);
            Updated?.Invoke(this, done);
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

        // Re-read: LinkReady may have written a folder id since, and losing it
        // here would make the retry create a second folder for the same guest.
        var current = _archive.All().FirstOrDefault(r => r.FolderName == record.FolderName)
            ?? record;
        var waiting = current with { UploadAttempts = attempts, UploadError = error };
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
        var current = _archive.All().FirstOrDefault(r => r.FolderName == record.FolderName)
            ?? record;

        var failed = current with
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

        Updated?.Invoke(this, failed);
    }
}
