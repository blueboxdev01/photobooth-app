using Photobooth.Core;
using Photobooth.Imaging;

namespace Photobooth.Imaging.Tests;

/// <summary>
/// Detection decides which side of the photos a template's art is drawn on, so
/// getting it wrong means a guest's faces are hidden. These cover the designs
/// that a naive "is it mostly opaque?" test would misread.
/// </summary>
public class ArtInspectorTests
{
    private static readonly TemplateCanvas Canvas = new(600, 1800, 300);

    private static readonly TemplateSlot[] Slots =
    [
        new(0.033, 0.017, 0.933, 0.233),
        new(0.033, 0.267, 0.933, 0.233),
        new(0.033, 0.517, 0.933, 0.233),
    ];

    [Fact]
    public void A_solid_image_is_a_backdrop()
    {
        var art = ArtInspector.Inspect(ArtFixtures.OpaquePng(Canvas), Canvas, Slots);

        Assert.Equal(ArtLayer.Behind, art.Layer);
    }

    [Fact]
    public void A_jpeg_is_always_a_backdrop()
    {
        // No alpha channel exists, so it cannot possibly be a frame.
        var art = ArtInspector.Inspect(ArtFixtures.Jpeg(Canvas), Canvas, Slots);

        Assert.Equal(ArtLayer.Behind, art.Layer);
        Assert.Equal(0, art.TransparentFractionInSlots);
    }

    [Fact]
    public void Art_with_clear_windows_over_the_slots_is_a_frame()
    {
        var art = ArtInspector.Inspect(ArtFixtures.FramePng(Canvas, Slots), Canvas, Slots);

        Assert.Equal(ArtLayer.InFront, art.Layer);
        Assert.True(art.TransparentFractionInSlots > 0.9,
            $"expected the slots to be almost entirely clear, got {art.TransparentFractionInSlots:P0}");
    }

    [Fact]
    public void A_frame_with_a_wide_solid_border_is_still_a_frame()
    {
        // The case a whole-image opacity test gets wrong, and the reason detection
        // samples inside the slots rather than across the image.
        //
        // These windows are small: three at 30% x 12% cover about a ninth of the
        // canvas, so the picture is ~89% opaque overall and a naive test calls it
        // a backdrop -- while it is ~100% clear exactly where the photos go.
        var wideBorder = new TemplateCanvas(1200, 1800, 300);
        TemplateSlot[] smallWindows =
        [
            new(0.35, 0.10, 0.30, 0.12),
            new(0.35, 0.30, 0.30, 0.12),
            new(0.35, 0.50, 0.30, 0.12),
        ];

        var art = ArtInspector.Inspect(
            ArtFixtures.FrameWithWideBorderPng(wideBorder, smallWindows),
            wideBorder,
            smallWindows);

        Assert.Equal(ArtLayer.InFront, art.Layer);

        var windowArea = smallWindows.Sum(s => s.W * s.H);
        Assert.True(windowArea < 0.15,
            $"the windows cover {windowArea:P0} of the canvas; this test only proves "
            + "anything while they are a small share of it");
    }

    [Fact]
    public void Transparency_that_misses_the_slots_does_not_make_a_frame()
    {
        // Soft or ragged edges are common in backdrops. Only transparency where
        // the photos go counts.
        var art = ArtInspector.Inspect(ArtFixtures.TransparentEdgesOnlyPng(Canvas), Canvas, Slots);

        Assert.Equal(ArtLayer.Behind, art.Layer);
    }

    [Fact]
    public void The_images_real_size_is_reported()
    {
        // Drives the "this is 1080x1080, the strip wants 600x1800" warning.
        var square = new TemplateCanvas(1080, 1080, 300);

        var art = ArtInspector.Inspect(ArtFixtures.OpaquePng(square), Canvas, Slots);

        Assert.Equal(1080, art.Width);
        Assert.Equal(1080, art.Height);
    }
}
