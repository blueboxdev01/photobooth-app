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

    public bool Enabled => _options.Enabled && auth.Configured;

    public bool Authorised => auth.Authorised;

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

            foreach (var photo in record.Photos.Where(p => !already.Contains(p)))
            {
                await UploadAsync(drive, folderId, Path.Combine(folder, photo),
                    photo, cancellationToken);
            }

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
        var metadata = new Google.Apis.Drive.v3.Data.File
        {
            // Same name as the folder on disk, so the two are trivially matched
            // up when a guest asks for their photos again a week later.
            Name = record.FolderName,
            MimeType = FolderMimeType,
            Parents = string.IsNullOrWhiteSpace(_options.ParentFolderId)
                ? null
                : [_options.ParentFolderId],
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
