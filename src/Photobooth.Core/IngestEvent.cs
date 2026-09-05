namespace Photobooth.Core;

public enum IngestOutcome
{
    Accepted,

    /// <summary>Seen and deliberately not used. <see cref="IngestEvent.Reason"/> says why.</summary>
    Rejected,

    /// <summary>Never became readable; given up on.</summary>
    Abandoned,
}

/// <summary>
/// One ingest decision, kept so a remote tester can send back what the app saw
/// rather than describing it.
///
/// Carries the file name and size but never the photo itself: this is the part
/// of the diagnostics that leaves the building, and guest photos should not.
/// </summary>
public sealed record IngestEvent(
    DateTimeOffset AtUtc,
    string FileName,
    IngestOutcome Outcome,
    string Reason,
    long SizeBytes);
