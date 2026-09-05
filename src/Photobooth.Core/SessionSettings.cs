namespace Photobooth.Core;

/// <summary>
/// Named Settings rather than Options because ASP.NET already has a
/// SessionOptions (cookie sessions) that is implicitly in scope throughout the
/// server project, and the collision forces an alias in every file that touches
/// both.
/// </summary>
public sealed class SessionSettings
{
    public const string SectionName = "Session";

    /// <summary>
    /// Photos per session.
    ///
    /// Temporary: from M4 this is derived from the selected template's slot count,
    /// so a 3-frame strip cannot get out of step with a 4-shot session. Until
    /// templates exist it is configured directly.
    /// </summary>
    public int ShotCount { get; set; } = 3;

    /// <summary>Advisory "3-2-1" shown before each pose.</summary>
    public int CountdownSeconds { get; set; } = 3;

    /// <summary>
    /// How long to wait for a photo before telling the operator something is
    /// wrong. Tuned from the measured press-to-file latency during field testing.
    /// </summary>
    public int NoPhotoTimeoutSeconds { get; set; } = 20;
}
