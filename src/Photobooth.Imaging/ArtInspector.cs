using Photobooth.Core;
using SkiaSharp;

namespace Photobooth.Imaging;

/// <summary>What an uploaded image turned out to be.</summary>
/// <param name="Layer">Whether it is a frame to draw over the photos, or a backdrop.</param>
/// <param name="Width">Actual pixel width, for reporting a size mismatch.</param>
/// <param name="Height">Actual pixel height.</param>
/// <param name="TransparentFractionInSlots">
/// How much of the photo area is see-through. Exposed so the UI can explain the
/// verdict rather than just assert it.
/// </param>
public sealed record ArtInspection(
    ArtLayer Layer,
    int Width,
    int Height,
    double TransparentFractionInSlots);

/// <summary>
/// Decides whether template art is a frame or a backdrop by looking at it.
///
/// The test is deliberately narrow: how transparent the image is **inside the
/// photo slots**. A frame is defined by having holes where the photos go, and
/// nothing else -- judging by overall opacity would misread the very common
/// design of a frame with a wide solid border, which is mostly opaque while
/// still being a frame.
/// </summary>
public static class ArtInspector
{
    /// <summary>Below this alpha a pixel counts as see-through.</summary>
    private const byte TransparentBelow = 250;

    /// <summary>
    /// Fraction of sampled slot pixels that must be see-through for the art to be
    /// a frame. Set well above zero so a few stray soft edges or a faint drop
    /// shadow cannot turn a backdrop into a frame.
    /// </summary>
    private const double FrameThreshold = 0.20;

    /// <summary>Samples per slot edge. 32x32 per slot is ample and costs nothing.</summary>
    private const int SamplesPerEdge = 32;

    public static ArtInspection Inspect(
        ReadOnlySpan<byte> imageBytes,
        TemplateCanvas canvas,
        IReadOnlyList<TemplateSlot> slots)
    {
        using var bitmap = SKBitmap.Decode(imageBytes.ToArray());
        if (bitmap is null)
        {
            // Undecodable art is rejected upstream; treat it as a backdrop so a
            // surprise here cannot hide photos behind an opaque frame.
            return new ArtInspection(ArtLayer.Behind, 0, 0, 0);
        }

        // No alpha channel at all -- a JPEG -- can only ever be a backdrop.
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            return new ArtInspection(ArtLayer.Behind, bitmap.Width, bitmap.Height, 0);
        }

        var sampled = 0;
        var transparent = 0;

        foreach (var slot in slots)
        {
            // Slots are fractions of the canvas; the art is fitted to that same
            // canvas, so the same fractions locate the slot within the image.
            var left = slot.X * bitmap.Width;
            var top = slot.Y * bitmap.Height;
            var width = slot.W * bitmap.Width;
            var height = slot.H * bitmap.Height;

            for (var iy = 0; iy < SamplesPerEdge; iy++)
            {
                for (var ix = 0; ix < SamplesPerEdge; ix++)
                {
                    var x = (int)(left + ((ix + 0.5) / SamplesPerEdge * width));
                    var y = (int)(top + ((iy + 0.5) / SamplesPerEdge * height));

                    if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
                    {
                        continue;
                    }

                    sampled++;
                    if (bitmap.GetPixel(x, y).Alpha < TransparentBelow)
                    {
                        transparent++;
                    }
                }
            }
        }

        var fraction = sampled == 0 ? 0 : transparent / (double)sampled;
        var layer = fraction >= FrameThreshold ? ArtLayer.InFront : ArtLayer.Behind;

        return new ArtInspection(layer, bitmap.Width, bitmap.Height, Math.Round(fraction, 3));
    }
}
