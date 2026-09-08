using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Photobooth.Delivery;

/// <summary>
/// Publishes a session as its own Google Drive folder, shared by link.
///
/// One folder per guest, rather than one gallery, is what makes the QR safe to
/// hand out: the link reaches that guest's photos and there is no id to edit to
/// reach anyone else's.
/// </summary>
public sealed class DrivePublisher(
    DriveAuth auth,
    IOptions<DriveOptions> options,
    ILogger<DrivePublisher> logger) : IGalleryPublisher
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly DriveOptions _options = options.Value;

    /// <summary>
    /// The parent folder's id, looked up once and kept. Resolving it per session
    /// would add a round trip to every guest's critical path for an answer that
    /// does not change.
    /// </summary>
    private string? _parentId;

    private readonly SemaphoreSlim _parentLock = new(1, 1);

    public bool Enabled => _options.Enabled && auth.Configured;

    public bool Authorised => auth.Authorised;

    /// <summary>
    /// Drop the remembered parent folder, so the next session looks it up again.
    /// Called when the operator renames it: otherwise sessions would keep going
    /// into the old folder until the app was restarted.
    /// </summary>
    public void ForgetParentFolder()
    {
        _parentLock.Wait();
        try
        {
            _parentId = null;
        }
        finally
        {
            _parentLock.Release();
        }
    }

    public async Task<PublishResult> PublishAsync(
        SessionRecord record,
        string folder,
        Action<string, string>? linkReady,
        CancellationToken cancellationToken)
    {
        try
        {
            var credential = await auth.CredentialAsync(cancellationToken);
            if (credential is null)
            {
                return PublishResult.Fail(
                    PublishFailure.NeedsAuthorisation,
                    "Not signed in to Google Drive. Open Setup and press Re-authorise.");
            }

            using var drive = auth.ServiceFor(credential);

            // Re-use the folder if a previous attempt got that far, so a retry
            // after a half-finished upload does not leave two folders behind.
            var resuming = record.DriveFolderId is not null;
            var folderId = record.DriveFolderId
                ?? await CreateFolderAsync(drive, record, cancellationToken);

            var url = $"https://drive.google.com/drive/folders/{folderId}";

            // On a retry, whatever the last attempt managed to upload is already
            // up there. Drive happily accepts two files with the same name, so
            // without this a guest ends up with their strip three times.
            var already = resuming
                ? await ExistingNamesAsync(drive, folderId, cancellationToken)
                : [];

            // The strip goes first, and the link is published the moment it
            // lands: the raws are the bulk of the megabytes, and a guest should
            // not have to wait through them for a code to appear.
            if (!already.Contains("strip.jpg"))
            {
                await UploadAsync(drive, folderId, Path.Combine(folder, record.Strip),
                    "strip.jpg", cancellationToken);
            }

            linkReady?.Invoke(folderId, url);

            // The raws go up together rather than one after another: they are
            // the bulk of a session and doing them in sequence left the folder
            // incomplete for three times longer than it needed to be.
            await UploadAllAsync(
                drive, folderId, folder,
                [.. record.Photos.Where(p => !already.Contains(p))],
                cancellationToken);

            logger.LogInformation(
                "Published {Session} as {Count} files in {Url}.",
                record.FolderName, record.Photos.Count + 1, url);

            return PublishResult.Success(folderId, url);
        }
        catch (TokenResponseException ex)
        {
            // The refresh token has been revoked or expired -- which is what a
            // consent screen left in Testing does after seven days.
            return PublishResult.Fail(
                PublishFailure.NeedsAuthorisation,
                $"Google rejected the saved sign-in ({ex.Error?.Error ?? ex.Message}). "
                + "Open Setup and press Re-authorise.");
        }
        catch (GoogleApiException ex)
        {
            return PublishResult.Fail(Classify(ex), Describe(ex));
        }
        catch (HttpRequestException ex)
        {
            return PublishResult.Fail(PublishFailure.Transient, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PublishResult.Fail(PublishFailure.Transient, "The upload timed out.");
        }
    }

    /// <summary>
    /// Upload several files at once, bounded so a big strip count cannot spam
    /// Drive past its rate limit.
    /// </summary>
    private async Task UploadAllAsync(
        DriveService drive,
        string folderId,
        string folder,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var slots = new SemaphoreSlim(Math.Max(1, _options.UploadConcurrency));

        var uploads = names.Select(async name =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                await UploadAsync(
                    drive, folderId, Path.Combine(folder, name), name, cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        });

        // WhenAll rather than a loop: one photo failing must not leave the others
        // half-done and unreported -- the queue retries the session as a whole,
        // and the already-there check keeps that from duplicating anything.
        await Task.WhenAll(uploads);
    }

    /// <summary>
    /// The folder every session folder is created inside, made by this app so the
    /// drive.file scope can actually write to it. Looked up by name once, then
    /// remembered.
    /// </summary>
    private async Task<string?> ParentFolderIdAsync(
        DriveService drive, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.ParentFolderId))
        {
            return _options.ParentFolderId;
        }

        if (string.IsNullOrWhiteSpace(_options.ParentFolderName))
        {
            return null;
        }

        if (_parentId is not null)
        {
            return _parentId;
        }

        await _parentLock.WaitAsync(cancellationToken);
        try
        {
            if (_parentId is not null)
            {
                return _parentId;
            }

            // An apostrophe in the folder name would otherwise close the quoted
            // string in the query and break the search -- "Ed's booth" is not an
            // unreasonable thing to call it.
            var name = _options.ParentFolderName.Replace("'", "\\'");
            var search = drive.Files.List();

            // drive.file means this only ever sees folders the app made, which is
            // exactly the one we are looking for.
            search.Q = $"mimeType = '{FolderMimeType}' and name = '{name}' "
                       + "and trashed = false";
            search.Fields = "files(id)";
            search.PageSize = 1;

            var found = await search.ExecuteAsync(cancellationToken);
            if (found.Files.Count > 0)
            {
                return _parentId = found.Files[0].Id;
            }

            var created = drive.Files.Create(new Google.Apis.Drive.v3.Data.File
            {
                Name = _options.ParentFolderName,
                MimeType = FolderMimeType,
            });
            created.Fields = "id";

            var folder = await created.ExecuteAsync(cancellationToken);
            logger.LogInformation(
                "Created the {Name} folder in Drive to keep sessions together.",
                _options.ParentFolderName);

            return _parentId = folder.Id;
        }
        finally
        {
            _parentLock.Release();
        }
    }

    /// <summary>What is already in the folder, so a retry does not duplicate it.</summary>
    private static async Task<HashSet<string>> ExistingNamesAsync(
        DriveService drive, string folderId, CancellationToken cancellationToken)
    {
        var request = drive.Files.List();
        request.Q = $"'{folderId}' in parents and trashed = false";
        request.Fields = "files(name)";
        request.PageSize = 100;

        var listed = await request.ExecuteAsync(cancellationToken);
        return [.. listed.Files.Select(f => f.Name)];
    }

    private async Task<string> CreateFolderAsync(
        DriveService drive, SessionRecord record, CancellationToken cancellationToken)
    {
        var parent = await ParentFolderIdAsync(drive, cancellationToken);

        var metadata = new Google.Apis.Drive.v3.Data.File
        {
            // Same name as the folder on disk, so the two are trivially matched
            // up when a guest asks for their photos again a week later.
            Name = record.FolderName,
            MimeType = FolderMimeType,
            Parents = parent is null ? null : [parent],
        };

        var request = drive.Files.Create(metadata);
        request.Fields = "id";
        var created = await request.ExecuteAsync(cancellationToken);

        // Anyone with the link can view: the normal photobooth bargain. A guest
        // forwarding it to family is the point, and the id is unguessable.
        await drive.Permissions
            .Create(new Permission { Type = "anyone", Role = "reader" }, created.Id)
            .ExecuteAsync(cancellationToken);

        return created.Id;
    }

    private static async Task UploadAsync(
        DriveService drive,
        string folderId,
        string path,
        string name,
        CancellationToken cancellationToken)
    {
        await using var stream = System.IO.File.OpenRead(path);

        var request = drive.Files.Create(
            new Google.Apis.Drive.v3.Data.File { Name = name, Parents = [folderId] },
            stream,
            "image/jpeg");
        request.Fields = "id";

        var progress = await request.UploadAsync(cancellationToken);
        if (progress.Exception is not null)
        {
            throw progress.Exception;
        }
    }

    /// <summary>
    /// Which failures are worth trying again. Getting this wrong in either
    /// direction is bad: retrying a full quota forever hides it, and giving up on
    /// a dropped wifi loses a guest their photos.
    /// </summary>
    private static PublishFailure Classify(GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason;

        if (reason is "storageQuotaExceeded")
        {
            return PublishFailure.QuotaExhausted;
        }

        if (reason is "authError" or "unauthorized" ||
            ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized)
        {
            return PublishFailure.NeedsAuthorisation;
        }

        // Rate limits and 5xx are the API asking us to come back later.
        if (reason is "rateLimitExceeded" or "userRateLimitExceeded" ||
            (int)ex.HttpStatusCode >= 500 ||
            ex.HttpStatusCode is System.Net.HttpStatusCode.TooManyRequests)
        {
            return PublishFailure.Transient;
        }

        return PublishFailure.Permanent;
    }

    private static string Describe(GoogleApiException ex) =>
        Classify(ex) == PublishFailure.QuotaExhausted
            ? "The booth's Google account is out of storage. Free some space or "
              + "upgrade the plan; the photos are safe on disk in the meantime."
            : ex.Error?.Message ?? ex.Message;
}
