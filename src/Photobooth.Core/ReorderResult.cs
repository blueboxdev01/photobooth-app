namespace Photobooth.Core;

/// <summary>
/// The outcome of rearranging the shots.
///
/// A rejected reorder still carries the current snapshot, so a console that got
/// its arithmetic wrong is corrected rather than left showing an order the strip
/// will not use.
/// </summary>
public sealed record ReorderResult(bool Ok, string? Error, SessionSnapshot Snapshot);
