namespace Photobooth.Delivery;

/// <summary>
/// Google Drive delivery.
///
/// <b>Off by default</b>, deliberately: the field-test build goes to somebody who
/// has the camera and no reason to hold our Google credentials, and a booth that
/// uploads nothing is a booth that cannot leak anything. Turning it on is a
/// decision someone makes at their own machine.
/// </summary>
public sealed class DriveOptions
{
    public const string SectionName = "Delivery:Drive";

    public bool Enabled { get; set; }

    /// <summary>
    /// Installed-app OAuth client. <b>Not a service account</b> -- those have no
    /// storage quota of their own and cannot upload to a personal My Drive at
    /// all, which is the most common way this whole pattern fails.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Not truly secret for an installed app, but it still never goes in the
    /// repo: it belongs in untracked appsettings.Local.json.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Where the refresh token is kept, encrypted with DPAPI. Under data/, which
    /// is gitignored.
    /// </summary>
    public string TokenStore { get; set; } = "data/drive-token.bin";

    /// <summary>
    /// Folder in the booth account's Drive that per-session folders are created
    /// inside. The app finds it by name and creates it if it is not there.
    ///
    /// It has to be a folder <b>this app made</b>: the drive.file scope reaches
    /// only files the app created, so a folder made by hand in the Drive web
    /// interface cannot be written into and uploads fail with "File not found".
    /// Empty puts every session folder loose in the root of My Drive, which one
    /// event turns into a hundred folders strewn through it.
    /// </summary>
    public string ParentFolderName { get; set; } = "Photobooth";

    /// <summary>
    /// An explicit parent, bypassing the lookup by name. Same scope rule applies:
    /// it must be a folder this app created, or writes into it will fail.
    /// </summary>
    public string? ParentFolderId { get; set; }

    /// <summary>
    /// How many raw photos are uploaded at once. Drive sustains about three
    /// writes a second, so three keeps a session's tail short without going near
    /// the rate limit.
    /// </summary>
    public int UploadConcurrency { get; set; } = 3;

    /// <summary>
    /// How long the queue waits before the first retry. Doubles each attempt,
    /// capped by <see cref="MaxBackoffSeconds"/>.
    /// </summary>
    public int BaseBackoffSeconds { get; set; } = 10;

    public int MaxBackoffSeconds { get; set; } = 300;

    /// <summary>
    /// Attempts before a session is parked as Failed and needs a person. It is
    /// not lost -- the photos are on disk and it can be re-published by hand.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>How often the queue looks for work when it has none.</summary>
    public int IdlePollSeconds { get; set; } = 15;
}
