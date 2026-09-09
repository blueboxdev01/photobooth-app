namespace Photobooth.Core;

/// <summary>
/// The outcome of retaking one shot.
///
/// A refusal still carries the current snapshot, so a console that asked for a
/// slot that is no longer there is corrected rather than left showing something
/// the session does not agree with.
/// </summary>
public sealed record RetakeResult(bool Ok, string? Error, SessionSnapshot Snapshot);
