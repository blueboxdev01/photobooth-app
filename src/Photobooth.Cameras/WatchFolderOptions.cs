namespace Photobooth.Cameras;

/// <summary>
/// Everything we currently <em>assume</em> about how EOS Utility writes files.
///
/// All of it is configuration rather than code on purpose: none of it has been
/// verified against a real camera yet, and the field test may well contradict
/// it. When it does, the fix should be a settings change, not a rewrite.
/// </summary>
public sealed class WatchFolderOptions
{
    public const string SectionName = "Camera:WatchFolder";

    /// <summary>Folder EOS Utility saves into. Created on start if missing.</summary>
    public string Path { get; set; } = "data/watch";

    /// <summary>
    /// Extensions treated as photos. Anything else is ignored -- notably .CR3
    /// sidecars if RAW ever gets switched on by accident.
    ///
    /// Starts empty on purpose: configuration binding *appends* to a collection
    /// that already holds defaults, so seeding this with [".jpg", ".jpeg"] and
    /// then configuring the same values yields four entries, and configuring
    /// only ".jpeg" would silently leave ".jpg" enabled. The fallback is applied
    /// after binding instead -- see DefaultExtensions.
    /// </summary>
    public string[] Extensions { get; set; } = [];

    public static readonly string[] DefaultExtensions = [".jpg", ".jpeg"];

    /// <summary>Gap between size samples when deciding a file is fully written.</summary>
    public int StabilityPollMilliseconds { get; set; } = 150;

    /// <summary>Consecutive identical size readings required before accepting a file.</summary>
    public int StabilityChecks { get; set; } = 3;

    /// <summary>Give up on a file that never settles, rather than blocking a session.</summary>
    public int CompletionTimeoutMilliseconds { get; set; } = 15_000;

    /// <summary>
    /// FileSystemWatcher drops events under load, so a periodic sweep re-scans the
    /// folder as a safety net. Set to 0 to disable.
    /// </summary>
    public int SweepIntervalMilliseconds { get; set; } = 2_000;

    /// <summary>Ignore zero-byte files; they are never a finished photo.</summary>
    public long MinimumFileSizeBytes { get; set; } = 1024;
}
