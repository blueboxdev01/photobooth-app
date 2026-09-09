namespace Photobooth.Delivery;

/// <summary>Where a session's delivery has got to. Stored in <c>session.json</c>.</summary>
public static class UploadStates
{
    /// <summary>Never tried, because delivery is switched off.</summary>
    public const string NotAttempted = "NotAttempted";

    /// <summary>Waiting for the queue, or waiting to be retried after a failure.</summary>
    public const string Pending = "Pending";

    /// <summary>Published. The record carries the folder id and the guest's link.</summary>
    public const string Uploaded = "Uploaded";

    /// <summary>
    /// Given up on: either the failure is not the kind retrying fixes, or it has
    /// been retried too many times. Needs a person, and says so on the console.
    /// </summary>
    public const string Failed = "Failed";
}

/// <summary>
/// Why a publish did not work, which decides whether trying again is sensible.
///
/// Kept separate from the message because the two are used differently: the
/// booth branches on this, and only a human reads the message.
/// </summary>
public enum PublishFailure
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>
    /// The network, a timeout, or Google having a bad minute. Retry: at an event
    /// this is nearly always a venue's wifi and it comes back.
    /// </summary>
    Transient,

    /// <summary>
    /// Not signed in, or the refresh token has been revoked or expired. Retrying
    /// cannot fix it; somebody has to press Re-authorise, so say so loudly.
    /// </summary>
    NeedsAuthorisation,

    /// <summary>
    /// The account is out of space. Retrying makes it no better, and the operator
    /// needs to know now rather than at the end of the night.
    /// </summary>
    QuotaExhausted,

    /// <summary>Something else. Not retried, on the grounds that we do not know.</summary>
    Permanent,
}

/// <summary>The outcome of publishing one session.</summary>
/// <param name="FolderId">The Drive folder id, for re-publishing into the same place.</param>
/// <param name="Url">What the QR encodes.</param>
public sealed record PublishResult(
    bool Ok,
    string? FolderId = null,
    string? Url = null,
    PublishFailure Failure = PublishFailure.None,
    string? Error = null,
    string? Qr = null)
{
    public static PublishResult Success(string folderId, string url, string? qr = null) =>
        new(true, folderId, url, Qr: qr);

    public static PublishResult Fail(PublishFailure failure, string error) =>
        new(false, Failure: failure, Error: error);

    /// <summary>Whether the queue should come back to this one later.</summary>
    public bool WorthRetrying => Failure is PublishFailure.Transient;
}

/// <summary>
/// Publishes a finished session somewhere a guest can reach it.
///
/// An interface rather than a direct dependency on Drive so the queue's retry and
/// failure handling -- which is where the bugs live -- is testable without a
/// Google account, a network, or anyone's quota. It also leaves room for the
/// branded gallery page to replace Drive later without touching the queue.
/// </summary>
public interface IGalleryPublisher
{
    /// <summary>False when delivery is switched off, as it is for the field-test build.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Whether a token is held. False means the next publish will fail with
    /// <see cref="PublishFailure.NeedsAuthorisation"/>, which the console shows
    /// before a guest is ever affected.
    /// </summary>
    bool Authorised { get; }

    /// <summary>
    /// Upload one session's folder. Implementations must upload the strip first,
    /// so the folder is never empty if a guest scans immediately.
    /// </summary>
    /// <param name="folder">The session's folder on disk, the source of truth.</param>
    /// <param name="linkReady">
    /// Called with (folderId, url) the moment the folder exists and the strip is
    /// in it -- before the raw photos, which are the bulk of the megabytes.
    ///
    /// This is what makes the QR appear while the guest is still standing there.
    /// A real session is around 25 MB, so waiting for the whole upload can easily
    /// outlast the guest on a venue's wifi, and a code that arrives after they
    /// have gone is the same as no code at all.
    /// </param>
    Task<PublishResult> PublishAsync(
        SessionRecord record,
        string folder,
        Action<string, string>? linkReady,
        CancellationToken cancellationToken);
}
