using Microsoft.Extensions.Options;
using Photobooth.Cameras;
using Photobooth.Core;

namespace Photobooth.Server;

public sealed record SettingsUpdate(
    string? WatchFolder,
    int? CountdownSeconds,
    int? NoPhotoTimeoutSeconds);

/// <summary>
/// Lets the operator point the app at the folder EOS Utility actually saves to,
/// and tune the two timings a field test is expected to correct.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", (
            SettingsStore store,
            WatchFolderCamera camera,
            IOptions<SessionSettings> session) => Results.Ok(new
        {
            watchFolder = camera.WatchFolderPath,
            countdownSeconds = session.Value.CountdownSeconds,
            noPhotoTimeoutSeconds = session.Value.NoPhotoTimeoutSeconds,
            settingsFile = store.Path,
            // Somewhere sensible to start from, since EOS Utility defaults into
            // the user's Pictures folder.
            suggestions = Suggestions(),
        }));

        app.MapPut("/api/settings", async (
            SettingsUpdate update,
            SettingsStore store,
            WatchFolderCamera camera,
            IOptions<SessionSettings> session) =>
        {
            var settings = store.Current;

            if (update.WatchFolder is { } requested)
            {
                var problem = ValidateFolder(requested, out var resolved);
                if (problem is not null)
                {
                    return Results.BadRequest(new { error = problem });
                }

                settings.WatchFolder = resolved;

                // Applied immediately: making someone restart to find out whether
                // they typed the right path would defeat the point.
                await camera.ChangeFolderAsync(resolved);
            }

            if (update.CountdownSeconds is { } countdown)
            {
                if (countdown is < 0 or > 30)
                {
                    return Results.BadRequest(new { error = "Countdown must be 0-30 seconds." });
                }

                settings.CountdownSeconds = countdown;
                session.Value.CountdownSeconds = countdown;
            }

            if (update.NoPhotoTimeoutSeconds is { } timeout)
            {
                if (timeout is < 5 or > 300)
                {
                    return Results.BadRequest(new
                    {
                        error = "The no-photo timeout must be 5-300 seconds.",
                    });
                }

                settings.NoPhotoTimeoutSeconds = timeout;
                session.Value.NoPhotoTimeoutSeconds = timeout;
            }

            store.Save(settings);

            return Results.Ok(new
            {
                watchFolder = camera.WatchFolderPath,
                countdownSeconds = session.Value.CountdownSeconds,
                noPhotoTimeoutSeconds = session.Value.NoPhotoTimeoutSeconds,
                cameraStatus = camera.Status.ToString(),
            });
        });

        // Checks a folder without committing to it, so the UI can say whether a
        // typed path is usable before anything changes.
        app.MapPost("/api/settings/check-folder", (SettingsUpdate update) =>
        {
            if (string.IsNullOrWhiteSpace(update.WatchFolder))
            {
                return Results.BadRequest(new { error = "No folder given." });
            }

            var problem = ValidateFolder(update.WatchFolder, out var resolved, create: false);
            return Results.Ok(new
            {
                path = resolved,
                ok = problem is null,
                error = problem,
                exists = Directory.Exists(resolved),
                willCreate = problem is null && !Directory.Exists(resolved),
                jpegCount = Directory.Exists(resolved)
                    ? Directory.EnumerateFiles(resolved, "*.jpg").Count()
                      + Directory.EnumerateFiles(resolved, "*.JPG").Count()
                    : 0,
            });
        });
    }

    /// <summary>
    /// Rejects a folder the app could not actually watch, and says why in terms
    /// the person typing it can act on.
    /// </summary>
    private static string? ValidateFolder(string folder, out string resolved, bool create = true)
    {
        resolved = folder;

        if (string.IsNullOrWhiteSpace(folder))
        {
            return "The folder cannot be blank.";
        }

        var expanded = Environment.ExpandEnvironmentVariables(folder.Trim());

        // Tested on the input, not on the result. GetFullPath resolves a relative
        // path against the app's own folder, so "Pictures" would come back rooted
        // and look perfectly valid while pointing somewhere nobody intended.
        if (!Path.IsPathRooted(expanded))
        {
            resolved = expanded;
            return "Use a full path, for example C:\\Users\\you\\Pictures\\Booth.";
        }

        try
        {
            resolved = Path.GetFullPath(expanded);
        }
        catch (Exception ex)
        {
            return $"That is not a usable path: {ex.Message}";
        }

        try
        {
            if (!Directory.Exists(resolved))
            {
                if (!create)
                {
                    // Saving will create it -- but only if it *can* be created.
                    // Reporting "usable" for a missing drive or a folder Windows
                    // will refuse would make the check worse than useless.
                    return CanBeCreated(resolved);
                }

                Directory.CreateDirectory(resolved);
            }

            // Writability is worth proving now rather than discovering mid-event.
            var probe = Path.Combine(resolved, $".pb-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Windows will not let this app write there. Pick a folder inside your user profile.";
        }
        catch (Exception ex)
        {
            return $"That folder cannot be used: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether a folder that does not exist yet could actually be made.
    ///
    /// Walks up to the nearest ancestor that does exist and tests writing there.
    /// That is what separates "will be created when you save" from a missing
    /// drive letter or a folder Windows will refuse without admin rights.
    /// </summary>
    private static string? CanBeCreated(string resolved)
    {
        var ancestor = Directory.GetParent(resolved);
        while (ancestor is not null && !ancestor.Exists)
        {
            ancestor = ancestor.Parent;
        }

        if (ancestor is null)
        {
            var root = Path.GetPathRoot(resolved);
            return $"{root} is not available. Check the drive letter.";
        }

        try
        {
            var probe = Path.Combine(ancestor.FullName, $".pb-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Windows will not let this app create folders in {ancestor.FullName}. " +
                   "Pick somewhere inside your user profile.";
        }
        catch (Exception ex)
        {
            return $"That folder cannot be created: {ex.Message}";
        }
    }

    private static string[] Suggestions()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return string.IsNullOrEmpty(pictures)
            ? []
            : [Path.Combine(pictures, "Photobooth"), pictures];
    }
}
