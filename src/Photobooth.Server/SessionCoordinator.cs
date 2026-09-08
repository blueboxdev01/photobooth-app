using Microsoft.AspNetCore.SignalR;
using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Delivery;
using Photobooth.Imaging;

namespace Photobooth.Server;

/// <summary>
/// The only place the camera, the session engine and the browsers meet.
///
/// Keeping the wiring here means <see cref="SessionEngine"/> stays free of
/// filesystem and network concerns, and the camera stays unaware that sessions
/// exist. Each side is testable on its own.
/// </summary>
public sealed class SessionCoordinator : IHostedService
{
    private readonly WatchFolderCamera _camera;
    private readonly SessionEngine _engine;
    private readonly IHubContext<SessionHub> _hub;
    private readonly TimeProvider _time;
    private readonly DiagnosticsService _diagnostics;
    private readonly StripCompositor _compositor;
    private readonly FileTemplateProvider _templates;
    private readonly SessionArchive _archive;
    private readonly UploadQueue _uploads;
    private readonly ILogger<SessionCoordinator> _logger;

    /// <summary>
    /// The session the guest screen is currently showing, so a delivery update
    /// can be matched to it. Uploads outlive the session that produced them --
    /// one can still be draining three guests later -- so "the QR is ready" has
    /// to say which session it is ready for.
    /// </summary>
    private string? _showing;

    // Generated when the session is armed so the archive folder and the eventual
    // QR link refer to the same session.
    private string _token = SessionArchive.NewToken();

    public SessionCoordinator(
        WatchFolderCamera camera,
        SessionEngine engine,
        IHubContext<SessionHub> hub,
        TimeProvider time,
        DiagnosticsService diagnostics,
        StripCompositor compositor,
        FileTemplateProvider templates,
        SessionArchive archive,
        UploadQueue uploads,
        ILogger<SessionCoordinator> logger)
    {
        _camera = camera;
        _engine = engine;
        _hub = hub;
        _time = time;
        _diagnostics = diagnostics;
        _compositor = compositor;
        _templates = templates;
        _archive = archive;
        _uploads = uploads;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _camera.PhotoArrived += OnPhotoArrived;
        _camera.StatusChanged += OnCameraStatus;
        _camera.IngestDecision += OnIngestDecision;
        _engine.Changed += OnSessionChanged;
        _uploads.Updated += OnDeliveryUpdated;

        // Nothing already sitting in the folder counts until a session starts.
        _camera.AcceptFrom = _time.GetUtcNow();
        await _camera.ConnectAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _camera.PhotoArrived -= OnPhotoArrived;
        _camera.StatusChanged -= OnCameraStatus;
        _camera.IngestDecision -= OnIngestDecision;
        _engine.Changed -= OnSessionChanged;
        _uploads.Updated -= OnDeliveryUpdated;
        await _camera.DisposeAsync();
    }

    /// <summary>
    /// Start a session. The camera's stale-file cutoff moves to now, so a photo
    /// left over from the previous guest can never appear on this strip.
    /// </summary>
    public SessionSnapshot Arm()
    {
        _camera.ResetSeen();
        _camera.AcceptFrom = _time.GetUtcNow();
        _token = SessionArchive.NewToken();
        _showing = null;
        return _engine.Arm();
    }

    private void OnPhotoArrived(object? sender, PhotoArrivedEventArgs e)
    {
        if (!_engine.SubmitPhoto(e.Photo))
        {
            // Not an error: the operator may be testing the camera between guests.
            _logger.LogInformation(
                "{File} arrived outside a session and was not used.", e.Photo.FileName);
        }
    }

    private void OnIngestDecision(object? sender, IngestEvent e) => _diagnostics.Record(e);

    private void OnSessionChanged(object? sender, SessionSnapshot snapshot)
    {
        Broadcast(snapshot);

        if (snapshot.State == SessionState.Composing)
        {
            // Off the caller's thread: this decodes several 24 MP JPEGs and must
            // not block the hub callback that just delivered the state change.
            _ = Task.Run(() => ComposeAsync(snapshot));
        }
    }

    /// <summary>
    /// Builds the strip and writes the session to disk.
    ///
    /// Order matters: compose, save locally, and only then (from M7) upload. The
    /// local copy is the source of truth, so a session survives anything the
    /// network or Google can do to it.
    /// </summary>
    private async Task ComposeAsync(SessionSnapshot snapshot)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"pb-strip-{Guid.NewGuid():N}.jpg");

        try
        {
            var template = _templates.Current;
            var photos = snapshot.Photos.Select(p => p.FilePath).ToList();

            await Task.Run(() => _compositor.Compose(
                template, photos, _templates.Folder, temp));

            var record = _archive.Save(
                _token, template, snapshot.Photos, temp,
                snapshot.StartedUtc ?? _time.GetUtcNow());

            // Queued, not awaited. The guest is standing there and the photos are
            // already safe on disk; a venue with no signal must cost them a QR
            // code, not their session.
            record = _uploads.Enqueue(record);
            _showing = record.FolderName;

            _engine.CompleteComposing(
                $"/api/sessions/{record.FolderName}/{record.Strip}", record.FolderName);

            BroadcastDelivery(record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Composing the strip failed.");
            _engine.FailComposing(
                $"Could not build the strip: {ex.Message}. The photos are safe -- " +
                "press Accept to try again.");
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A session's delivery changed -- its link became usable, or the upload
    /// finished or gave up. Push it, so the guest screen swaps "preparing your
    /// link" for the QR without anyone refreshing anything.
    /// </summary>
    private void OnDeliveryUpdated(object? sender, SessionRecord record) =>
        BroadcastDelivery(record);

    private void BroadcastDelivery(SessionRecord record)
    {
        var status = _uploads.Status();

        _ = _hub.Clients.All.SendAsync(SessionHub.DeliveryMessage, new DeliveryUpdate(
                status.Enabled,
                status.Authorised,
                status.Pending,
                status.Failed,
                status.LastError,
                record.FolderName,
                record.UploadState,
                record.DriveUrl,
                record.DriveUrl is null ? null : $"/api/sessions/{record.FolderName}/qr.png",
                record.UploadError))
            .ContinueWith(
                t => _logger.LogWarning(t.Exception, "Failed to push delivery state."),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>The delivery state of the session the screens are showing.</summary>
    public DeliveryUpdate CurrentDelivery()
    {
        var status = _uploads.Status();
        var record = _showing is null
            ? null
            : _archive.All().FirstOrDefault(r => r.FolderName == _showing);

        return new DeliveryUpdate(
            status.Enabled,
            status.Authorised,
            status.Pending,
            status.Failed,
            status.LastError,
            record?.FolderName,
            record?.UploadState,
            record?.DriveUrl,
            record?.DriveUrl is null ? null : $"/api/sessions/{record.FolderName}/qr.png",
            record?.UploadError);
    }

    private void OnCameraStatus(object? sender, CameraStatusEventArgs e)
    {
        _logger.LogInformation("Camera {Status}: {Message}", e.Status, e.Message);
        Broadcast(_engine.Snapshot);
    }

    private void Broadcast(SessionSnapshot snapshot)
    {
        // Fire and forget: a slow or disconnected browser must never stall ingest.
        _ = _hub.Clients.All.SendAsync(SessionHub.StateMessage, snapshot)
            .ContinueWith(
                t => _logger.LogWarning(t.Exception, "Failed to push session state."),
                TaskContinuationOptions.OnlyOnFaulted);
    }
}

/// <summary>
/// What both screens are told about delivery.
///
/// Deliberately not part of <see cref="SessionSnapshot"/>: an upload outlives the
/// session that produced it, so folding it into the session state would either
/// hold a finished session open until the network came back, or lose track of an
/// upload the moment the next guest stepped in. Hence <see cref="SessionFolder"/>
/// -- the screens match it against the session they are showing.
/// </summary>
public sealed record DeliveryUpdate(
    bool Enabled,
    bool Authorised,
    int Pending,
    int Failed,
    string? LastError,
    string? SessionFolder,
    string? State,
    string? Url,
    string? QrUrl,
    string? Error);
