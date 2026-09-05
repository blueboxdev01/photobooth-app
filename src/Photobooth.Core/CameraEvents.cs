namespace Photobooth.Core;

/// <summary>Raised when a photo has arrived and been verified as complete.</summary>
public sealed class PhotoArrivedEventArgs(CapturedPhoto photo) : EventArgs
{
    public CapturedPhoto Photo { get; } = photo;
}

public enum CameraStatus
{
    /// <summary>Not started, or shut down.</summary>
    Disconnected,

    /// <summary>Watching, and the source looks healthy.</summary>
    Ready,

    /// <summary>
    /// Watching, but something is wrong that the operator must see -- the watch
    /// folder vanished, or is not writable.
    /// </summary>
    Faulted,
}

/// <summary>A change in camera/ingest health, for the operator screen.</summary>
public sealed class CameraStatusEventArgs(CameraStatus status, string? message = null) : EventArgs
{
    public CameraStatus Status { get; } = status;
    public string? Message { get; } = message;
}
