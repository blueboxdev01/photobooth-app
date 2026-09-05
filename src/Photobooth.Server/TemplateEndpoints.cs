using Microsoft.Extensions.Options;
using Photobooth.Cameras;
using Photobooth.Core;
using Photobooth.Imaging;

namespace Photobooth.Server;

/// <summary>
/// The template editor's API.
///
/// The one that matters is the preview: it renders through the *real*
/// <see cref="StripCompositor"/> with real sample photos, so what the editor
/// shows is what a session will actually produce. A browser-side approximation
/// would drift from the compositor the moment either changed, and the drift
/// would only show up on a printed strip.
/// </summary>
public static class TemplateEndpoints
{
    private const int MaxOverlayBytes = 12 * 1024 * 1024;

    public static void MapTemplateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/templates", (FileTemplateProvider templates) => Results.Ok(new
        {
            selected = templates.Current.Name,
            source = templates.Source,
            usingBuiltInFallback = templates.UsingFallback,
            folder = templates.Folder,
            available = templates.Available(),
            current = templates.Current,
        }));

        app.MapGet("/api/templates/{name}", (string name, FileTemplateProvider templates) =>
        {
            if (!FileTemplateProvider.IsValidName(name))
            {
                return Results.BadRequest(new { error = "Invalid template name." });
            }

            var path = Path.Combine(templates.Folder, name + ".json");
            return File.Exists(path)
                ? Results.Content(File.ReadAllText(path), "application/json")
                : Results.NotFound();
        });

        app.MapPut("/api/templates/{name}", (
            string name, StripTemplate template, FileTemplateProvider templates) =>
        {
            var problem = Validate(template);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            try
            {
                return Results.Ok(templates.Save(name, template));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/templates/{name}/select", (string name, FileTemplateProvider templates) =>
        {
            if (!FileTemplateProvider.IsValidName(name))
            {
                return Results.BadRequest(new { error = "Invalid template name." });
            }

            var selected = templates.Select(name);
            return Results.Ok(new
            {
                selected = selected.Name,
                // Changing template changes how many photos the next session takes.
                shotCount = selected.ShotCount,
                usingBuiltInFallback = templates.UsingFallback,
            });
        });

        app.MapPost("/api/templates/{name}/overlay", async (
            string name, HttpRequest request, FileTemplateProvider templates) =>
        {
            if (!request.HasFormContentType)
            {
                return Results.BadRequest(new { error = "Expected a file upload." });
            }

            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("overlay") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "No file was uploaded." });
            }

            if (file.Length > MaxOverlayBytes)
            {
                return Results.BadRequest(new
                {
                    error = $"The frame is {file.Length / 1024 / 1024} MB; the limit is " +
                            $"{MaxOverlayBytes / 1024 / 1024} MB.",
                });
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);

            try
            {
                var saved = templates.SaveOverlay(name, buffer.ToArray());
                return Results.Ok(new { overlay = saved, bytes = buffer.Length });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/templates/{name}/overlay", (string name, FileTemplateProvider templates) =>
        {
            var path = templates.OverlayPath(name);
            return path is null ? Results.NotFound() : Results.File(path, "image/png");
        });

        // Sample photos, so the editor can show what the crop does to a real face
        // rather than to a grey rectangle.
        app.MapGet("/api/samples", (IOptions<MockEosUtilityOptions> mockOptions) =>
        {
            var folder = mockOptions.Value.SourceFolder;
            if (!Directory.Exists(folder))
            {
                return Results.Ok(Array.Empty<string>());
            }

            return Results.Ok(Directory.EnumerateFiles(folder, "*.jpg")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"/api/samples/{Uri.EscapeDataString(Path.GetFileName(f))}")
                .ToArray());
        });

        app.MapGet("/api/samples/{file}", (
            string file, IOptions<MockEosUtilityOptions> mockOptions) =>
        {
            if (file != Path.GetFileName(file) || file.Contains(".."))
            {
                return Results.BadRequest();
            }

            var path = Path.Combine(mockOptions.Value.SourceFolder, file);
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });

        // Renders the template through the real compositor with sample photos, so
        // the editor can show exactly what a session will produce.
        app.MapPost("/api/templates/preview", (
            StripTemplate template,
            FileTemplateProvider templates,
            StripCompositor compositor,
            IOptions<MockEosUtilityOptions> mockOptions) =>
        {
            var problem = Validate(template);
            if (problem is not null)
            {
                return Results.BadRequest(new { error = problem });
            }

            var samples = SamplePhotos(mockOptions.Value.SourceFolder, template.Slots.Count);
            if (samples.Count == 0)
            {
                return Results.BadRequest(new
                {
                    error = $"No sample photos in {mockOptions.Value.SourceFolder} to preview with.",
                });
            }

            var temp = Path.Combine(Path.GetTempPath(), $"pb-preview-{Guid.NewGuid():N}.jpg");
            try
            {
                compositor.Compose(template, samples, templates.Folder, temp, jpegQuality: 82);
                var bytes = File.ReadAllBytes(temp);
                return Results.File(bytes, "image/jpeg");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        });
    }

    /// <summary>Sample photos, repeated if the template has more slots than samples.</summary>
    private static List<string> SamplePhotos(string folder, int count)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var available = Directory.EnumerateFiles(folder, "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return available.Count == 0
            ? []
            : [.. Enumerable.Range(0, count).Select(i => available[i % available.Count])];
    }

    /// <summary>
    /// Rejects templates that would produce a broken strip. Slots may sit partly
    /// outside the canvas -- a frame that bleeds off the edge is a legitimate
    /// design -- but a canvas of zero, or no slots at all, is not.
    /// </summary>
    private static string? Validate(StripTemplate? template)
    {
        if (template is null)
        {
            return "No template was supplied.";
        }

        if (template.Canvas.Width is < 100 or > 10_000
            || template.Canvas.Height is < 100 or > 10_000)
        {
            return "The canvas must be between 100 and 10000 pixels on each side.";
        }

        if (template.Canvas.Dpi is < 72 or > 1200)
        {
            return "DPI must be between 72 and 1200.";
        }

        if (template.Slots.Count is 0 or > 12)
        {
            return "A template needs between 1 and 12 photo slots.";
        }

        if (template.Slots.Any(s => s.W <= 0 || s.H <= 0))
        {
            return "Every slot needs a width and a height.";
        }

        return null;
    }
}
