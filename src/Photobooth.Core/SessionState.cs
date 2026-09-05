namespace Photobooth.Core;

/// <summary>
/// Where a session currently is.
///
/// Note what is missing: there is no "selecting" state. Every photo captured
/// goes on the strip, and the template's slot count decides how many that is.
/// Composing, Uploading and ShowQr are declared here but not yet reachable --
/// they are wired up in later milestones and listed now so the shape of the
/// flow is visible in one place.
/// </summary>
public enum SessionState
{
    /// <summary>Between guests. The attract screen.</summary>
    Idle,

    /// <summary>
    /// Advisory countdown before a pose. Advisory because the app cannot fire the
    /// shutter -- the guest sees "3-2-1" and the operator presses the remote, and
    /// the two can drift apart.
    /// </summary>
    Countdown,

    /// <summary>Waiting for the next photo to land in the watch folder.</summary>
    Collecting,

    /// <summary>
    /// No photo arrived within the window. With an external trigger the app cannot
    /// tell "not pressed yet" from "camera asleep", so it says so rather than
    /// hanging.
    /// </summary>
    TimedOut,

    /// <summary>All shots captured; guest and operator look them over.</summary>
    ReviewShots,

    /// <summary>Building the strip. Wired up in M4.</summary>
    Composing,

    /// <summary>Uploading to Drive. Wired up in M7.</summary>
    Uploading,

    /// <summary>QR on the guest screen. Wired up in M7.</summary>
    ShowQr,

    /// <summary>Finished. Returns to Idle when the operator starts the next guest.</summary>
    Done,
}
