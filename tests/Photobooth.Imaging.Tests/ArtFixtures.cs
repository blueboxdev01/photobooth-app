using Photobooth.Core;
using SkiaSharp;

namespace Photobooth.Imaging.Tests;

/// <summary>
/// Template art generated in-memory, so the detection tests exercise real decoded
/// pixels rather than hand-waved byte arrays.
/// </summary>
internal static class ArtFixtures
{
    /// <summary>A backdrop: solid colour everywhere, no transparency at all.</summary>
    public static byte[] OpaquePng(TemplateCanvas canvas) =>
        Png(canvas, surface => surface.Canvas.Clear(new SKColor(0x2B, 0x1B, 0x4D)));

    /// <summary>The same thing as a JPEG, which cannot carry transparency.</summary>
    public static byte[] Jpeg(TemplateCanvas canvas)
    {
        var info = new SKImageInfo(canvas.Width, canvas.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(new SKColor(0x2B, 0x1B, 0x4D));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>A frame: opaque everywhere except clear windows over the slots.</summary>
    public static byte[] FramePng(TemplateCanvas canvas, IReadOnlyList<TemplateSlot> slots) =>
        Png(canvas, surface =>
        {
            surface.Canvas.Clear(new SKColor(0x10, 0x10, 0x10));
            PunchHoles(surface.Canvas, canvas, slots, inset: 0);
        });

    /// <summary>
    /// A frame with a wide solid border -- mostly opaque overall, but still clear
    /// where the photos go. The design that a whole-image opacity test misreads.
    /// </summary>
    public static byte[] FrameWithWideBorderPng(
        TemplateCanvas canvas, IReadOnlyList<TemplateSlot> slots) =>
        Png(canvas, surface =>
        {
            surface.Canvas.Clear(new SKColor(0x8B, 0x1E, 0x3F));
            PunchHoles(surface.Canvas, canvas, slots, inset: 0);
        });

    /// <summary>
    /// Transparent only in the margins, opaque across every slot: a backdrop with
    /// soft edges, not a frame.
    /// </summary>
    public static byte[] TransparentEdgesOnlyPng(TemplateCanvas canvas) =>
        Png(canvas, surface =>
        {
            using var paint = new SKPaint { Color = new SKColor(0x20, 0x40, 0x80) };
            // Leaves a clear band around the outside, well away from the slots.
            surface.Canvas.DrawRect(
                new SKRect(0, canvas.Height * 0.01f, canvas.Width, canvas.Height * 0.80f),
                paint);
        });

    private static void PunchHoles(
        SKCanvas canvas, TemplateCanvas size, IReadOnlyList<TemplateSlot> slots, float inset)
    {
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear };
        foreach (var slot in slots)
        {
            var (x, y, w, h) = slot.ToPixels(size);
            canvas.DrawRect(new SKRect(x + inset, y + inset, x + w - inset, y + h - inset), clear);
        }
    }

    private static byte[] Png(TemplateCanvas canvas, Action<SKSurface> draw)
    {
        var info = new SKImageInfo(
            canvas.Width, canvas.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
