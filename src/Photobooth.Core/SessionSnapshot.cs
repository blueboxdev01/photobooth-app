namespace Photobooth.Core;

/// <summary>
/// The whole of what both screens need to render, in one immutable value.
///
/// Deadlines are sent as absolute instants rather than "seconds remaining" so the
/// browser can tick the countdown down locally instead of the server pushing an
/// update every second.
/// </summary>
public sealed record SessionSnapshot(
    SessionState State,
    int ShotCount,
    IReadOnlyList<CapturedPhoto> Photos,
    DateTimeOffset? CountdownEndsUtc,
    DateTimeOffset? TimeoutAtUtc,
    DateTimeOffset? StartedUtc,
    string? Message,
    string? StripUrl = null,
    string? SessionFolder = null)
{
    public int CapturedCount => Photos.Count;

    /// <summary>1-based index of the pose currently being taken.</summary>
    public int CurrentShot => Math.Min(Photos.Count + 1, ShotCount);

    public static SessionSnapshot Idle(int shotCount) =>
        new(SessionState.Idle, shotCount, [], null, null, null, null);
}
