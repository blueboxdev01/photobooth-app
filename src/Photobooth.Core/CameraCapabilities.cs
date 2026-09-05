namespace Photobooth.Core;

/// <summary>
/// What a given camera adapter can actually do. The operator UI greys out
/// controls an adapter cannot honour rather than offering them and failing.
/// </summary>
/// <param name="CanTrigger">
/// False for every v1 path: the shutter is fired by a physical remote, so the
/// app can only observe photos arriving, never request one.
/// </param>
/// <param name="CanSetExposure">False unless a real SDK adapter is in use.</param>
/// <param name="HasLiveView">
/// False for watch-folder ingest. Guest preview comes from <c>IPreviewSource</c>
/// (a separate webcam), which is deliberately not the capture device.
/// </param>
public sealed record CameraCapabilities(
    bool CanTrigger,
    bool CanSetExposure,
    bool HasLiveView)
{
    /// <summary>Capabilities of a folder-watching adapter: observe only.</summary>
    public static CameraCapabilities ObserveOnly { get; } = new(false, false, false);
}
