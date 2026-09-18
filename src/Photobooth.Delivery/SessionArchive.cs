using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Photobooth.Core;

namespace Photobooth.Delivery;

public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    /// <summary>Root of the per-session archive.</summary>
    public string Folder { get; set; } = "data/sessions";

    /// <summary>Warn on the operator screen below this much free space.</summary>
    public long LowDiskWarningBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// What a finished session left on disk. Mirrors <c>session.json</c>.
///
/// The upload fields are not just a report -- they *are* the delivery queue. A
/// session waiting to be published is one whose <see cref="UploadState"/> says
/// so, which means the queue needs no database of its own, cannot disagree with
/// the archive, survives a crash without doing anything, and can be unstuck by
/// editing a text file.
/// </summary>
public sealed record SessionRecord(
    string Token,
    string FolderName,
    DateTimeOffset CreatedUtc,
    string Template,
    int ShotCount,
    string Strip,
    IReadOnlyList<string> Photos,
    IReadOnlyList<string> SourceFiles,
    string UploadState = UploadStates.NotAttempted,
    string? DriveFolderId = null,
    string? DriveUrl = null,
    int UploadAttempts = 0,
    string? UploadError = null,
    /// <summary>
    /// The QR image, once there is a link for it to point at. Kept beside the
    /// photos so a guest who lost their link can be shown the code again days
    /// later, without the booth having to be running.
    /// </summary>
    string? Qr = null,
    /// <summary>
    /// The looping GIF of the strip, if one was made. Null for every session
    /// archived before the feature existed, and for any session where building
    /// it failed -- which is why it is nullable and last in this list. A
    /// required parameter here would stop every existing session.json on disk
    /// from deserialising.
    /// </summary>
    string? Animation = null);

/// <summary>
/// Writes each session to its own folder on disk.
///
/// Local disk is the source of truth; Drive (M7) receives a copy of exactly this
/// folder under the same name. Composing and saving locally before any upload is
/// attempted means the session survives a revoked token, a full quota, a deleted
/// account, or a venue with no signal.
/// </summary>
public sealed class SessionArchive(
    IOptions<ArchiveOptions> options,
    ILogger<SessionArchive> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // camelCase to match the HTTP API, since the gallery page will read this
        // file directly. Case-insensitive on the way back in so records written
        // before this choice still load.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ArchiveOptions _options = options.Value;

    /// <summary>
    /// What each session.json held, and what the file looked like when we read it.
    ///
    /// <para>
    /// Both screens poll delivery every few seconds and all of it goes through
    /// <see cref="All"/> -- which opened and parsed every session.json on disk,
    /// every time. That is fixed work per session, so it grows all evening:
    /// measured at 5ms over 8 sessions and 35ms over 200. By the end of a long
    /// night the booth is re-reading hours of finished sessions to answer "is this
    /// one delivered yet".
    /// </para>
    ///
    /// <para>
    /// <b>Revalidated, not trusted.</b> The entry is reused only while the file's
    /// write time and length both still match. This is not caution for its own
    /// sake: the archive is deliberately a pile of text files precisely so a stuck
    /// session can be unstuck by editing one by hand, and the runbook tells the
    /// operator to do exactly that. A cache that held on to what it read first
    /// would quietly make that advice wrong.
    /// </para>
    /// <para>
    /// A record already read is also what answers while a writer is replacing
    /// the file, so a delivery poll landing in that gap no longer loses the
    /// session -- which is stronger than the uncached version managed.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _cacheLock = new();

    private readonly record struct Cached(DateTime WrittenUtc, long Length, SessionRecord Record);

    public string Root => Path.GetFullPath(_options.Folder);

    /// <summary>
    /// An unguessable, URL-safe session id.
    ///
    /// Random rather than sequential because it ends up in the QR link: a guest
    /// must not be able to reach anyone else's photos by editing the URL.
    /// </summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[9];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Copies the captures out of the watch folder, saves the strip beside them,
    /// and records what happened.
    /// </summary>
    /// <param name="stripSource">
    /// The composed strip, typically written to a temporary path first.
    /// </param>
    public SessionRecord Save(
        string token,
        StripTemplate template,
        IReadOnlyList<CapturedPhoto> captures,
        string stripSource,
        DateTimeOffset createdUtc,
        string? animationSource = null)
    {
        var folderName = FolderName(createdUtc, token);
        var folder = Path.Combine(Root, folderName);
        Directory.CreateDirectory(folder);

        var photoNames = new List<string>();
        var sourceNames = new List<string>();

        for (var i = 0; i < captures.Count; i++)
        {
            var capture = captures[i];
            var name = $"photo-{i + 1}{Path.GetExtension(capture.FileName).ToLowerInvariant()}";

            // Copy, never move: the watch folder belongs to EOS Utility, and taking
            // files out from under it invites trouble.
            File.Copy(capture.FilePath, Path.Combine(folder, name), overwrite: true);

            var copied = new FileInfo(Path.Combine(folder, name));
            if (copied.Length != capture.SizeBytes)
            {
                logger.LogWarning(
                    "{Name} copied as {Copied} bytes but the capture was {Original}.",
                    name, copied.Length, capture.SizeBytes);
            }

            photoNames.Add(name);
            sourceNames.Add(capture.FileName);
        }

        const string stripName = "strip.jpg";
        File.Copy(stripSource, Path.Combine(folder, stripName), overwrite: true);

        // Named to pair with the strip it is a moving copy of.
        string? animationName = null;
        if (animationSource is not null && File.Exists(animationSource))
        {
            animationName = "strip.gif";
            File.Copy(animationSource, Path.Combine(folder, animationName), overwrite: true);
        }

        var record = new SessionRecord(
            token, folderName, createdUtc, template.Name, template.ShotCount,
            stripName, photoNames, sourceNames, Animation: animationName);

        WriteRecord(folder, record);

        logger.LogInformation(
            "Archived session {Folder}: {Count} photos, the strip{Animation}.",
            folderName, photoNames.Count,
            animationName is null ? string.Empty : " and the animation");

        return record;
    }

    /// <summary>
    /// Replace a session's record.
    ///
    /// Written to a temporary file and moved into place rather than written over
    /// the top. Both screens poll the delivery status every few seconds, and that
    /// reads every session.json -- so a plain overwrite races the readers: on
    /// Windows the write fails with a sharing violation, which the upload queue
    /// then mistakes for a failed upload and retries. Moving is also what stops a
    /// reader seeing a half-written file.
    /// </summary>
    public void WriteRecord(string folder, SessionRecord record)
    {
        var path = Path.Combine(folder, "session.json");
        var staging = path + ".tmp";

        File.WriteAllText(staging, JsonSerializer.Serialize(record, Json));

        // Windows will not replace a file another handle has open, however
        // politely that handle shared it -- and a reader only holds it for
        // microseconds, so the collision is worth waiting out rather than
        // reporting. Virus scanners produce the same thing on the same file.
        const int attempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staging, path, overwrite: true);

                // Dropped rather than replaced: the next read takes the file's own
                // write time with it. Explicit here because we are the writer and
                // need not wait for a clock to disagree -- the revalidation in All()
                // is for edits made outside this process.
                lock (_cacheLock)
                {
                    _cache.Remove(Path.GetFileName(
                        folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                }

                return;
            }
            catch (Exception ex) when (
                (ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>
    /// Read a file that something else may be replacing at this moment.
    ///
    /// <c>FileShare.Delete</c> is the part that matters: without it a reader
    /// blocks the move above, and the writer is the one that fails.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public string FolderFor(SessionRecord record) => Path.Combine(Root, record.FolderName);

    /// <summary>Sessions on disk, newest first. Used to re-publish after an event.</summary>
    public IReadOnlyList<SessionRecord> All()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var records = new List<SessionRecord>();

        lock (_cacheLock)
        {
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(folder);
                var path = Path.Combine(folder, "session.json");
                var before = new FileInfo(path);

                // May be default, whose Record is null -- that is the "never read
                // this one" case, and it is checked for rather than assumed away.
                _cache.TryGetValue(name, out var cached);

                if (!before.Exists)
                {
                    // The folder is here but the record is not, which is what a
                    // replace in flight looks like from the outside. A session we
                    // have read before is not lost because we looked in the gap.
                    if (cached.Record is not null)
                    {
                        present.Add(name);
                        records.Add(cached.Record);
                    }

                    continue;
                }

                present.Add(name);

                // Length as well as write time, because a timestamp alone is only
                // as fine-grained as the filesystem: FAT32, which is what a USB
                // stick used as an output folder is likely to be, rounds to two
                // seconds -- and the upload queue can rewrite a record twice
                // inside that.
                if (cached.Record is not null
                    && cached.WrittenUtc == before.LastWriteTimeUtc
                    && cached.Length == before.Length)
                {
                    records.Add(cached.Record);
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<SessionRecord>(
                        ReadShared(path), Json)
                        ?? throw new JsonException($"{path} contained null.");

                    // Stat again, and keep the entry only if the file did not move
                    // underneath the read. Otherwise the stat taken before it and
                    // the content taken after it describe different versions, and
                    // the cache would answer a later matching stat with the wrong
                    // record. Unremembered here just means read again next time.
                    var after = new FileInfo(path);
                    if (after.Exists
                        && after.LastWriteTimeUtc == before.LastWriteTimeUtc
                        && after.Length == before.Length)
                    {
                        _cache[name] = new Cached(before.LastWriteTimeUtc, before.Length, record);
                    }

                    records.Add(record);
                }
                catch (Exception ex)
                {
                    if (cached.Record is not null)
                    {
                        // Almost certainly the writer mid-replace. The copy we
                        // already have is a better answer than dropping the session
                        // out of the list -- a delivery poll that loses a session
                        // reports it as gone, and the screens act on that.
                        records.Add(cached.Record);
                    }
                    else
                    {
                        logger.LogWarning(ex, "Could not read {Path}.", path);
                    }
                }
            }

            // A session whose folder is gone stops being remembered -- otherwise the
            // booth would hold every session of the evening in memory.
            foreach (var gone in _cache.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _cache.Remove(gone);
            }
        }

        return [.. records.OrderByDescending(r => r.CreatedUtc)];
    }

    public long? FreeDiskBytes()
    {
        try
        {
            Directory.CreateDirectory(Root);
            return new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    public bool DiskIsLow() => FreeDiskBytes() is { } free && free < _options.LowDiskWarningBytes;

    /// <summary>Sortable, human-readable, and unique: 2026-09-05_1942_a7f3c2.</summary>
    private static string FolderName(DateTimeOffset at, string token)
    {
        var safe = new string(token.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToLowerInvariant();
        return $"{at.ToLocalTime():yyyy-MM-dd_HHmm}_{safe}";
    }
}
