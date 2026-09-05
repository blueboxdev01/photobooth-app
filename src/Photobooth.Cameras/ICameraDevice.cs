using Photobooth.Core;

namespace Photobooth.Cameras;

/// <summary>
/// A source of photos.
///
/// Note the shape: a photo <em>arrives</em> as an event; it is not the return
/// value of a capture call. In v1 the shutter is fired by a physical remote and
/// the app only ever observes files appearing, so an adapter can honestly report
/// <see cref="CameraCapabilities.CanTrigger"/> = false. Modelling capture as
/// Task&lt;Photo&gt; CaptureAsync() would bake in the assumption that the app
/// drives the shutter and force every v1 adapter to fake a return value.
/// </summary>
public interface ICameraDevice : IAsyncDisposable
{
    CameraCapabilities Capabilities { get; }

    CameraStatus Status { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ask the camera to take a photo. A no-op on adapters that cannot trigger;
    /// callers must check <see cref="CameraCapabilities.CanTrigger"/> first.
    /// </summary>
    Task RequestCaptureAsync(CancellationToken cancellationToken = default);

    event EventHandler<PhotoArrivedEventArgs>? PhotoArrived;

    event EventHandler<CameraStatusEventArgs>? StatusChanged;
}
