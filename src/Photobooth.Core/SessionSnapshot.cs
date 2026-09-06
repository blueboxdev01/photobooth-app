namespace Photobooth.Core;

/// <summary>
/// The whole of what both screens need to render, in one immutable value.
///
/// Deadlines are sent as absolute instants rather than "seconds remaining" so the
/// browser can tick the countdown down locally instead of the server pushing an
/// update every second.
/// </summary>
/// <param name="Photos">
/// The shots in the order they will be composited -- which is not necessarily the
/// order they were taken in, once the operator has rearranged them.
/// </param>
/// <param name="Order">
/// For each entry in <paramref name="Photos"/>, the 0-based position it was
/// captured in. Lets the console label a thumbnail "shot 4" after it has been
/// dragged to the front, so the operator can see what moved where.
/// </param>
public sealed record SessionSnapshot(
    SessionState State,
    int ShotCount,
    IReadOnlyList<CapturedPhoto> Photos,
    IReadOnlyList<int> Order,
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

    /// <summary>
    /// True once the operator has rearranged the shots, so the console can offer
    /// to put them back.
    /// </summary>
    public bool IsReordered => Order.Where((capture, position) => capture != position).Any();

    public static SessionSnapshot Idle(int shotCount) =>
        new(SessionState.Idle, shotCount, [], [], null, null, null, null);
}
