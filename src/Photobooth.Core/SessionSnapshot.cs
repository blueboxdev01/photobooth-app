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
/// <param name="RetakingSlot">
/// The 0-based strip position being reshot, when one is. Lets both screens name
/// the pose rather than showing a bare countdown, and is what stops a retake of
/// photo two being announced as photo four.
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
    string? SessionFolder = null,
    int? RetakingSlot = null,
    /// <summary>
    /// What the booth’s clock said when this was built.
    ///
    /// <para>
    /// Here because a countdown must not be computed against the screen’s own
    /// clock. The operator console runs on the booth, so its clock agrees by
    /// definition; a second screen elsewhere has a clock of its own, and one
    /// three seconds out showed the operator three seconds and the guest six.
    /// Both screens work out how far their clock is from this and count the
    /// same seconds regardless.
    /// </para>
    /// </summary>
    DateTimeOffset? ServerNowUtc = null)
{
    public int CapturedCount => Photos.Count;

    /// <summary>
    /// 1-based index of the pose currently being taken.
    ///
    /// During a retake that is the slot being redone, not the next empty one --
    /// telling a guest "photo 4 of 4" while they are reshooting photo 2 is worse
    /// than saying nothing.
    /// </summary>
    public int CurrentShot => RetakingSlot is { } slot
        ? Math.Min(slot + 1, ShotCount)
        : Math.Min(Photos.Count + 1, ShotCount);

    /// <summary>
    /// True once the operator has rearranged the shots, so the console can offer
    /// to put them back.
    /// </summary>
    public bool IsReordered => Order.Where((capture, position) => capture != position).Any();

    public static SessionSnapshot Idle(int shotCount) =>
        new(SessionState.Idle, shotCount, [], [], null, null, null, null);
}
