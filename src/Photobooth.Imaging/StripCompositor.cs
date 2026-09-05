using Microsoft.Extensions.Logging;
using Photobooth.Core;
using SkiaSharp;

namespace Photobooth.Imaging;

/// <summary>
/// Draws the finished strip: background, photos into their slots, then the frame
/// art on top.
/// </summary>
public sealed class StripCompositor(ILogger<StripCompositor> logger)
{
    /// <summary>
    /// Composites <paramref name="photoPaths"/> into the template and writes a JPEG.
    /// </summary>
    /// <param name="templateFolder">
    /// Where the overlay named by the template lives.
    /// </param>
    public void Compose(
        StripTemplate template,
        IReadOnlyList<string> photoPaths,
        string templateFolder,
        string outputPath,
        int jpegQuality = 92)
    {
        if (photoPaths.Count != template.Slots.Count)
        {
            // A mismatch means the session and the template disagree about how many
            // photos there are, which should be impossible now that the template
            // decides the shot count -- so fail loudly rather than half-fill a strip.
            throw new ArgumentException(
                $"Template '{template.Name}' has {template.Slots.Count} slots but " +
                $"{photoPaths.Count} photos were supplied.", nameof(photoPaths));
        }

        var canvasInfo = new SKImageInfo(
            template.Canvas.Width, template.Canvas.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(canvasInfo);
        var canvas = surface.Canvas;
        canvas.Clear(ParseColour(template.Background));

        for (var i = 0; i < template.Slots.Count; i++)
        {
            DrawSlot(canvas, template, template.Slots[i], photoPaths[i]);
        }

        DrawOverlay(canvas, template, templateFolder);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var file = File.Create(outputPath))
        {
            data.SaveTo(file);
        }

        // Skia does not write JPEG density, and a strip that prints at the wrong
        // physical size is the single most likely printing complaint. Patch the
        // JFIF header so 600x1800 really means 2x6 inches at 300 DPI.
        JpegDensity.Stamp(outputPath, template.Canvas.Dpi);

        logger.LogInformation(
            "Composed {Output} ({Width}x{Height} at {Dpi} DPI, {Slots} photos)",
            Path.GetFileName(outputPath), template.Canvas.Width, template.Canvas.Height,
            template.Canvas.Dpi, template.Slots.Count);
    }

    private void DrawSlot(SKCanvas canvas, StripTemplate template, TemplateSlot slot, string photoPath)
    {
        var (x, y, w, h) = slot.ToPixels(template.Canvas);
        var target = new SKRect(x, y, x + w, y + h);

        using var bitmap = SKBitmap.Decode(photoPath);
        if (bitmap is null)
        {
            logger.LogWarning("Could not decode {Photo}; leaving its slot empty.", photoPath);
            return;
        }

        var source = slot.Fit == SlotFit.Cover
            ? CoverCrop(bitmap.Width, bitmap.Height, w / (float)h)
            : new SKRect(0, 0, bitmap.Width, bitmap.Height);

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, source, target, paint);
    }

    /// <summary>
    /// The centre region of the photo matching the slot's aspect ratio.
    ///
    /// This is where the R50's 3:2 frame loses its sides to a 4:3 slot -- the
    /// reason the guest screen shows a crop guide rather than the camera's full
    /// frame.
    /// </summary>
    internal static SKRect CoverCrop(int width, int height, float targetAspect)
    {
        var sourceAspect = width / (float)height;

        if (sourceAspect > targetAspect)
        {
            // Source is wider: trim the left and right.
            var cropWidth = height * targetAspect;
            var sideInset = (width - cropWidth) / 2f;
            return new SKRect(sideInset, 0, width - sideInset, height);
        }

        // Source is taller: trim the top and bottom.
        var cropHeight = width / targetAspect;
        var topInset = (height - cropHeight) / 2f;
        return new SKRect(0, topInset, width, height - topInset);
    }

    private void DrawOverlay(SKCanvas canvas, StripTemplate template, string templateFolder)
    {
        if (string.IsNullOrWhiteSpace(template.Overlay))
        {
            return;
        }

        var path = Path.Combine(templateFolder, template.Overlay);
        if (!File.Exists(path))
        {
            logger.LogWarning("Overlay {Overlay} not found; the strip will have no frame art.", path);
            return;
        }

        using var overlay = SKBitmap.Decode(path);
        if (overlay is null)
        {
            logger.LogWarning("Overlay {Overlay} could not be decoded.", path);
            return;
        }

        var full = new SKRect(0, 0, template.Canvas.Width, template.Canvas.Height);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(overlay, new SKRect(0, 0, overlay.Width, overlay.Height), full, paint);
    }

    private static SKColor ParseColour(string value) =>
        SKColor.TryParse(value, out var colour) ? colour : SKColors.White;
}
