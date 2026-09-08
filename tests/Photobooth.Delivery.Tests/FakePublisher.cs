using Photobooth.Delivery;

namespace Photobooth.Delivery.Tests;

/// <summary>
/// A publisher that fails exactly as instructed.
///
/// The whole point of <see cref="IGalleryPublisher"/> being an interface: the
/// retry and give-up behaviour is where the bugs are, and it can be pinned down
/// here without a Google account, a network, or anyone's quota.
/// </summary>
public sealed class FakePublisher : IGalleryPublisher
{
    private readonly Queue<PublishResult> _scripted = new();

    public bool Enabled { get; set; } = true;
    public bool Authorised { get; set; } = true;

    /// <summary>Every (folderName, folderPath) it was asked to publish, in order.</summary>
    public List<(string Folder, string Path)> Calls { get; } = [];

    /// <summary>Thrown instead of returning, to prove a throw is not fatal.</summary>
    public Exception? Throws { get; set; }

    /// <summary>
    /// Announce the link before failing or returning, as the real publisher does
    /// once the folder and strip are up.
    /// </summary>
    public bool AnnouncesLink { get; set; } = true;

    /// <summary>Used once each, in order; then <see cref="Default"/> takes over.</summary>
    public FakePublisher Script(params PublishResult[] results)
    {
        foreach (var r in results)
        {
            _scripted.Enqueue(r);
        }

        return this;
    }

    public PublishResult Default { get; set; } =
        PublishResult.Success("folder-id", "https://drive.example/folder-id");

    /// <summary>Held open so a test can observe an upload still in flight.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public Task<PublishResult> PublishAsync(
        SessionRecord record,
        string folder,
        Action<string, string>? linkReady,
        CancellationToken cancellationToken)
    {
        Calls.Add((record.FolderName, folder));

        if (AnnouncesLink)
        {
            var id = record.DriveFolderId ?? $"drive-{record.FolderName}";
            linkReady?.Invoke(id, $"https://drive.example/{id}");
        }

        if (Throws is { } ex)
        {
            throw ex;
        }

        if (Gate is not null)
        {
            return WaitThenAnswer(record);
        }

        var result = _scripted.Count > 0 ? _scripted.Dequeue() : Default;

        // A real publisher gives each session its own folder; mirroring that here
        // keeps the "two sessions get different links" assertion meaningful.
        if (result.Ok && result.FolderId == "folder-id")
        {
            result = PublishResult.Success(
                $"drive-{record.FolderName}",
                $"https://drive.example/drive-{record.FolderName}");
        }

        return Task.FromResult(result);
    }

    private async Task<PublishResult> WaitThenAnswer(SessionRecord record)
    {
        await Gate!.Task;
        var result = _scripted.Count > 0 ? _scripted.Dequeue() : Default;

        if (result.Ok && result.FolderId == "folder-id")
        {
            result = PublishResult.Success(
                $"drive-{record.FolderName}",
                $"https://drive.example/drive-{record.FolderName}");
        }

        return result;
    }
}
