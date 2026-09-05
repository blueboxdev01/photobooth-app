using Microsoft.AspNetCore.SignalR;
using Photobooth.Cameras;
using Photobooth.Core;

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
    private readonly ILogger<SessionCoordinator> _logger;

    public SessionCoordinator(
        WatchFolderCamera camera,
        SessionEngine engine,
        IHubContext<SessionHub> hub,
        TimeProvider time,
        ILogger<SessionCoordinator> logger)
    {
        _camera = camera;
        _engine = engine;
        _hub = hub;
        _time = time;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _camera.PhotoArrived += OnPhotoArrived;
        _camera.StatusChanged += OnCameraStatus;
        _engine.Changed += OnSessionChanged;

        // Nothing already sitting in the folder counts until a session starts.
        _camera.AcceptFrom = _time.GetUtcNow();
        await _camera.ConnectAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _camera.PhotoArrived -= OnPhotoArrived;
        _camera.StatusChanged -= OnCameraStatus;
        _engine.Changed -= OnSessionChanged;
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

    private void OnSessionChanged(object? sender, SessionSnapshot snapshot) =>
        Broadcast(snapshot);

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
