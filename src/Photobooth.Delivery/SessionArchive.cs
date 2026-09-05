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

/// <summary>What a finished session left on disk. Mirrors <c>session.json</c>.</summary>
public sealed record SessionRecord(
    string Token,
    string FolderName,
    DateTimeOffset CreatedUtc,
    string Template,
    int ShotCount,
    string Strip,
    IReadOnlyList<string> Photos,
    IReadOnlyList<string> SourceFiles,
    string UploadState = "NotAttempted",
    string? DriveFolderId = null,
    string? DriveUrl = null);

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
        DateTimeOffset createdUtc)
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

        var record = new SessionRecord(
            token, folderName, createdUtc, template.Name, template.ShotCount,
            stripName, photoNames, sourceNames);

        WriteRecord(folder, record);

        logger.LogInformation(
            "Archived session {Folder}: {Count} photos plus the strip.",
            folderName, photoNames.Count);

        return record;
    }

    public void WriteRecord(string folder, SessionRecord record) =>
        File.WriteAllText(
            Path.Combine(folder, "session.json"),
            JsonSerializer.Serialize(record, Json));

    public string FolderFor(SessionRecord record) => Path.Combine(Root, record.FolderName);

    /// <summary>Sessions on disk, newest first. Used to re-publish after an event.</summary>
    public IReadOnlyList<SessionRecord> All()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var records = new List<SessionRecord>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(folder, "session.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<SessionRecord>(
                    File.ReadAllText(path), Json);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read {Path}.", path);
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
