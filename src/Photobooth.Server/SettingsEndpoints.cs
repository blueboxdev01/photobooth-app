using Microsoft.Extensions.Options;
using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Delivery;
using Photobooth.Imaging;

namespace Photobooth.Server;

/// <summary>Outcome of a layout change: what went wrong, and what it cost.</summary>
internal sealed record LayoutResult(string? Error, bool OverlayDetached = false);

public sealed record SettingsUpdate(
    string? WatchFolder,
    string? OutputFolder,
    int? CountdownSeconds,
    int? NoPhotoTimeoutSeconds,
    int? MinPhotos,
    int? MaxPhotos,
    int? PhotoCount,
    string? CanvasPresetId,
    string? DisplayBackgroundColor,
    bool? ClearDisplayBackgroundImage);

/// <summary>
/// Everything an operator sets up per event: where the camera's photos arrive,
/// where finished sessions are filed, how many photos a strip holds, what shape
/// it is, and what the guest screen looks like.
/// </summary>
public static class SettingsEndpoints
{
    private const int MaxBackgroundBytes = 16 * 1024 * 1024;

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", (
            SettingsStore store,
            WatchFolderCamera camera,
            SessionArchive archive,
            FileTemplateProvider templates,
            IOptions<SessionSettings> session) =>
        {
            var current = templates.Current;
            return Results.Ok(new
            {
                watchFolder = camera.WatchFolderPath,
                outputFolder = archive.Root,
                countdownSeconds = session.Value.CountdownSeconds,
                noPhotoTimeoutSeconds = session.Value.NoPhotoTimeoutSeconds,
                settingsFile = store.Path,
                suggestions = Suggestions(),

                layout = new
                {
                    minPhotos = MinPhotos(store),
                    maxPhotos = MaxPhotos(store),
                    photoCount = current.ShotCount,
                    supportedMin = SlotLayout.MinPhotos,
                    supportedMax = SlotLayout.MaxPhotos,
                    canvasPresetId = CanvasPresets.Matching(current.Canvas)?.Id,
                    orientation = SlotLayout.OrientationOf(current.Canvas).ToString(),
                    canvas = current.Canvas,
                    template = current.Name,
                    presets = CanvasPresets.All.Select(p => new
                    {
                        p.Id,
                        p.Label,
                        p.Inches,
                        orientation = p.Orientation.ToString(),
                        p.Canvas.Width,
                        p.Canvas.Height,
                    }),
                },

                display = new
                {
                    backgroundColor = store.Current.DisplayBackgroundColor ?? "#14161A",
                    backgroundImage = store.Current.DisplayBackgroundImage is null
                        ? null
                        : "/api/settings/display-background",
                },
            });
        });

        app.MapPut("/api/settings", async (
            SettingsUpdate update,
            SettingsStore store,
            WatchFolderCamera camera,
            SessionArchive archive,
            FileTemplateProvider templates,
            IOptions<ArchiveOptions> archiveOptions,
            IOptions<SessionSettings> session) =>
        {
            // Staged on a copy and validated in full before anything is applied.
            // Mutating the live settings as we went meant a rejected request could
            // still leave the camera re-pointed and the photo bounds changed, and
            // the next successful save would write those to disk.
            var settings = store.Current.Clone();

            string? newWatchFolder = null;
            string? newOutputFolder = null;

            if (update.WatchFolder is { } watch)
            {
                var problem = ValidateFolder(watch, out var resolved);
                if (problem is not null)
                {
                    return Results.BadRequest(new { error = problem });
                }

                settings.WatchFolder = newWatchFolder = resolved;
            }

            if (update.OutputFolder is { } output)
            {
                var problem = ValidateFolder(output, out var resolved);
                if (problem is not null)
                {
                    return Results.BadRequest(new { error = problem });
                }

                // Compared against the watch folder this request would leave in
                // place, not the one currently in force.
                var watchAfter = newWatchFolder ?? camera.WatchFolderPath;
                if (string.Equals(resolved, watchAfter, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new
                    {
                        error = "The output folder must differ from the watch folder, or "
                              + "finished sessions would be mixed in with the camera's raw files.",
                    });
                }

                settings.OutputFolder = newOutputFolder = resolved;
            }

            if (update.CountdownSeconds is { } countdown)
            {
                if (countdown is < 0 or > 30)
                {
                    return Results.BadRequest(new { error = "Countdown must be 0-30 seconds." });
                }

                settings.CountdownSeconds = countdown;
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
            }

            if (update.DisplayBackgroundColor is { } colour)
            {
                if (!LooksLikeHexColour(colour))
                {
                    return Results.BadRequest(new
                    {
                        error = "The background colour must be a hex value such as #1A2B3C.",
                    });
                }

                settings.DisplayBackgroundColor = colour.ToUpperInvariant();
            }

            // Checked before anything is committed; the template is only rewritten
            // in the apply pass below.
            var layout = ApplyLayout(update, settings, templates, write: false);
            if (layout.Error is not null)
            {
                return Results.BadRequest(new { error = layout.Error });
            }

            // ---- everything validated, so apply ----

            if (newWatchFolder is not null)
            {
                // Applied immediately: making someone restart to find out whether
                // they typed the right path would defeat the point.
                await camera.ChangeFolderAsync(newWatchFolder);
            }

            if (newOutputFolder is not null)
            {
                archiveOptions.Value.Folder = newOutputFolder;
            }

            if (settings.CountdownSeconds is { } appliedCountdown)
            {
                session.Value.CountdownSeconds = appliedCountdown;
            }

            if (settings.NoPhotoTimeoutSeconds is { } appliedTimeout)
            {
                session.Value.NoPhotoTimeoutSeconds = appliedTimeout;
            }

            if (update.ClearDisplayBackgroundImage == true)
            {
                DeleteBackgroundImage(store, settings);
            }

            layout = ApplyLayout(update, settings, templates, write: true);
            store.Save(settings);

            var current = templates.Current;
            return Results.Ok(new
            {
                watchFolder = camera.WatchFolderPath,
                outputFolder = archive.Root,
                countdownSeconds = session.Value.CountdownSeconds,
                noPhotoTimeoutSeconds = session.Value.NoPhotoTimeoutSeconds,
                photoCount = current.ShotCount,
                orientation = SlotLayout.OrientationOf(current.Canvas).ToString(),
                canvas = current.Canvas,
                cameraStatus = camera.Status.ToString(),
                overlayDetached = layout.OverlayDetached,
                note = layout.OverlayDetached
                    ? "The frame art was drawn for the previous layout, so it has been "
                      + "detached. Upload art for the new size in the template editor."
                    : null,
            });
        });

        // Checks a folder without committing to it, so the UI can say whether a
        // typed path is usable before anything changes.
        app.MapPost("/api/settings/check-folder", (SettingsUpdate update) =>
        {
            var folder = update.WatchFolder ?? update.OutputFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                return Results.BadRequest(new { error = "No folder given." });
            }

            var problem = ValidateFolder(folder, out var resolved, create: false);
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

        app.MapPost("/api/settings/display-background", async (
            HttpRequest request, SettingsStore store) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a file upload." });
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("background") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No file was uploaded." });
            }

            if (file.Length > MaxBackgroundBytes)
            {
                return Results.BadRequest(new
                {
                    error = $"That image is {file.Length / 1024 / 1024} MB; the limit is "
                          + $"{MaxBackgroundBytes / 1024 / 1024} MB.",
                });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            var bytes = buffer.ToArray();

            var extension = ImageExtension(bytes);
            if (extension is null)
            {
                return Results.BadRequest(new
                {
                    error = "The backdrop must be a PNG or JPEG image.",
                });
            }

            var settings = store.Current;
            DeleteBackgroundImage(store, settings);

            var name = "display-background" + extension;
            Directory.CreateDirectory(BrandingFolder(store));
            await File.WriteAllBytesAsync(Path.Combine(BrandingFolder(store), name), bytes);

            settings.DisplayBackgroundImage = name;
            store.Save(settings);

            return Results.Ok(new { image = "/api/settings/display-background", bytes = bytes.Length });
        });

        app.MapGet("/api/settings/display-background", (SettingsStore store) =>
        {
            var name = store.Current.DisplayBackgroundImage;
            if (name is null)
            {
                return Results.NotFound();
            }

            var path = Path.Combine(BrandingFolder(store), name);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var type = Path.GetExtension(path).ToLowerInvariant() == ".png"
                ? "image/png"
                : "image/jpeg";
            return Results.File(path, type);
        });
    }

    private static int MinPhotos(SettingsStore store) =>
        store.Current.MinPhotos ?? 2;

    private static int MaxPhotos(SettingsStore store) =>
        store.Current.MaxPhotos ?? 6;

    /// <summary>
    /// Applies the photo count and output size by regenerating the active
    /// template's slots.
    ///
    /// The template still decides the shot count -- that invariant is what stops
    /// a three-frame strip pairing with a four-shot session. This just gives the
    /// operator a way to drive the template from a number instead of by dragging
    /// rectangles.
    /// </summary>
    /// <param name="write">
    /// False validates only, leaving both the settings copy and the template file
    /// untouched. The endpoint runs this pass first so a rejected request cannot
    /// have already changed the bounds or rewritten a template.
    /// </param>
    private static LayoutResult ApplyLayout(
        SettingsUpdate update,
        BoothSettings settings,
        FileTemplateProvider templates,
        bool write)
    {
        var wantsBounds = update.MinPhotos is not null || update.MaxPhotos is not null;
        var wantsLayout = update.PhotoCount is not null || update.CanvasPresetId is not null;

        if (!wantsBounds && !wantsLayout)
        {
            return new LayoutResult(null);
        }

        var min = update.MinPhotos ?? settings.MinPhotos ?? 2;
        var max = update.MaxPhotos ?? settings.MaxPhotos ?? 6;

        if (min < SlotLayout.MinPhotos || max > SlotLayout.MaxPhotos)
        {
            return new LayoutResult($"Photo counts must be between {SlotLayout.MinPhotos} and "
                 + $"{SlotLayout.MaxPhotos}.");
        }

        if (min > max)
        {
            return new LayoutResult("The minimum number of photos cannot exceed the maximum.");
        }

        var current = templates.Current;
        var count = update.PhotoCount ?? settings.PhotoCount ?? current.ShotCount;

        if (count < min || count > max)
        {
            return new LayoutResult($"With this event set to {min}-{max} photos, {count} is out of range.");
        }

        var canvas = current.Canvas;
        if (update.CanvasPresetId is { } presetId)
        {
            var preset = CanvasPresets.Find(presetId);
            if (preset is null)
            {
                return new LayoutResult($"Unknown output size '{presetId}'.");
            }

            canvas = preset.Canvas;
        }

        if (!write)
        {
            return new LayoutResult(null);
        }

        settings.MinPhotos = min;
        settings.MaxPhotos = max;
        settings.PhotoCount = count;
        if (update.CanvasPresetId is not null)
        {
            settings.CanvasPresetId = CanvasPresets.Find(update.CanvasPresetId)!.Id;
        }

        // Regenerated rather than nudged: the operator asked for a number of
        // photos on a given canvas, and even placement is the whole point.
        var slots = SlotLayout.Arrange(count, canvas);
        var name = TemplateFileName(templates);

        // Frame art is drawn to fit one canvas and one arrangement of photos.
        // Left attached across a re-layout it is stretched over the new slots --
        // a 2x6 strip's footer smeared across a 6x4 landscape print, with its old
        // slot borders landing in the wrong places. Detach it rather than render
        // that. The PNG is untouched on disk and can be re-attached in the editor.
        var reshaped = canvas != current.Canvas || slots.Count != current.Slots.Count;
        var detached = reshaped && current.Overlay is not null;

        templates.Save(name, current with
        {
            Canvas = canvas,
            Slots = slots,
            Overlay = reshaped ? null : current.Overlay,
        });

        return new LayoutResult(null, detached);
    }

    /// <summary>
    /// The file the active template is stored under.
    ///
    /// Falls back to a fresh name when the built-in template is in force, so
    /// regenerating a layout after a missing template file writes a real one
    /// rather than silently changing nothing.
    /// </summary>
    private static string TemplateFileName(FileTemplateProvider templates)
    {
        if (!templates.UsingFallback)
        {
            var name = Path.GetFileNameWithoutExtension(templates.Source);
            if (FileTemplateProvider.IsValidName(name))
            {
                return name;
            }
        }

        return "event-layout";
    }

    private static string BrandingFolder(SettingsStore store) =>
        Path.Combine(Path.GetDirectoryName(store.Path)!, "branding");

    private static void DeleteBackgroundImage(SettingsStore store, BoothSettings settings)
    {
        if (settings.DisplayBackgroundImage is not { } existing)
        {
            return;
        }

        try
        {
            var path = Path.Combine(BrandingFolder(store), existing);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A leftover file is not worth failing the request over.
        }

        settings.DisplayBackgroundImage = null;
    }

    /// <summary>PNG and JPEG magic numbers; the extension a browser sends is not evidence.</summary>
    private static string? ImageExtension(ReadOnlySpan<byte> data)
    {
        if (data.Length > 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ".png";
        }

        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }

        return null;
    }

    private static bool LooksLikeHexColour(string value) =>
        value.Length is 4 or 7
        && value[0] == '#'
        && value[1..].All(Uri.IsHexDigit);

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
