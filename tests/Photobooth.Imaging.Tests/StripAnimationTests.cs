using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Core;
using Photobooth.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace Photobooth.Imaging.Tests;

/// <summary>
/// The looping GIF of a strip.
///
/// <para>
/// A session has three or four stills and no burst capture, so the motion comes
/// from rotating the photos through the slots. These tests state that rotation
/// as a property rather than checking pixels against a reference image: the
/// claim is <b>every photo appears in every slot exactly once over the loop, and
/// no two slots ever show the same photo</b>, and that is exactly what an
/// off-by-one or a wrong modulo breaks.
/// </para>
/// </summary>
public sealed class StripAnimationTests : IDisposable
{
    private readonly string _work =
        Path.Combine(Path.GetTempPath(), "pb-anim", Guid.NewGuid().ToString("N"));

    private readonly StripCompositor _compositor =
        new(NullLogger<StripCompositor>.Instance);

    public StripAnimationTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Three slots in a row, big enough that the canvas gets shrunk.</summary>
    private static StripTemplate ThreeAcross() => new(
        "anim-test",
        new TemplateCanvas(2000, 600),
        [
            new TemplateSlot(0, 0, 1 / 3.0, 1),
            new TemplateSlot(1 / 3.0, 0, 1 / 3.0, 1),
            new TemplateSlot(2 / 3.0, 0, 1 / 3.0, 1),
        ]);

    private static readonly SKColor[] Palette = [SKColors.Red, SKColors.Lime, SKColors.Blue];

    /// <summary>One flat colour per photo, so which photo is where is unmistakable.</summary>
    private List<string> SolidPhotos()
    {
        var paths = new List<string>();

        for (var i = 0; i < Palette.Length; i++)
        {
            using var bitmap = new SKBitmap(400, 300, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(Palette[i]);
            }

            var path = Path.Combine(_work, $"solid-{i}.png");
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
            paths.Add(path);
        }

        return paths;
    }

    private string Animate(AnimationOptions? options = null)
    {
        var path = Path.Combine(_work, "strip.gif");
        _compositor.ComposeAnimation(ThreeAcross(), SolidPhotos(), _work, path, options);
        return path;
    }

    /// <summary>
    /// Which of the three source colours a pixel is. Classified by dominant
    /// channel rather than exact equality, because GIF's 256-colour palette is
    /// reached through a quantiser and need not return the exact bytes.
    /// </summary>
    private static int ColourIndex(Rgba32 pixel)
    {
        if (pixel.R > pixel.G && pixel.R > pixel.B) return 0;   // red
        if (pixel.G > pixel.R && pixel.G > pixel.B) return 1;   // green
        if (pixel.B > pixel.R && pixel.B > pixel.G) return 2;   // blue
        return -1;
    }

    /// <summary>The colour in each slot, for each frame of the decoded GIF.</summary>
    private static int[][] SlotColours(Image<Rgba32> gif, StripTemplate template)
    {
        var canvas = StripCompositor.Shrink(template.Canvas, AnimationOptions.Default.LongestEdge);

        var result = new int[gif.Frames.Count][];

        for (var f = 0; f < gif.Frames.Count; f++)
        {
            var frame = gif.Frames[f];
            var colours = new int[template.Slots.Count];

            for (var s = 0; s < template.Slots.Count; s++)
            {
                var (x, y, w, h) = template.Slots[s].ToPixels(canvas);
                colours[s] = ColourIndex(frame[x + (w / 2), y + (h / 2)]);
            }

            result[f] = colours;
        }

        return result;
    }

    // --- the rotation ---------------------------------------------------------

    /// <summary>
    /// Two slots showing the same photo at once is what a wrong modulo produces,
    /// and it would look obviously broken on the guest's screen.
    /// </summary>
    [Fact]
    public void No_two_slots_ever_show_the_same_photo()
    {
        using var gif = Image.Load<Rgba32>(Animate());

        var frames = SlotColours(gif, ThreeAcross());

        for (var f = 0; f < frames.Length; f++)
        {
            Assert.DoesNotContain(-1, frames[f]);
            Assert.True(frames[f].Distinct().Count() == frames[f].Length,
                $"frame {f + 1} shows slots {string.Join(", ", frames[f])} -- "
                + "two slots are displaying the same photo at the same moment.");
        }
    }

    /// <summary>
    /// The other half of the claim: over one loop, each slot shows every photo.
    /// Without this, a "rotation" that only swapped two of three would pass the
    /// test above while leaving one photo never seen in one slot.
    /// </summary>
    [Fact]
    public void Every_photo_visits_every_slot_exactly_once()
    {
        var template = ThreeAcross();
        using var gif = Image.Load<Rgba32>(Animate());

        var frames = SlotColours(gif, template);

        for (var slot = 0; slot < template.Slots.Count; slot++)
        {
            var seen = frames.Select(f => f[slot]).OrderBy(c => c).ToArray();

            Assert.Equal(Enumerable.Range(0, Palette.Length).ToArray(), seen);
        }
    }

    /// <summary>
    /// Frame one is the printed strip exactly, so the loop opens on the picture
    /// the guest has already been handed rather than a shuffled version of it.
    /// </summary>
    [Fact]
    public void The_first_frame_is_the_strip_as_printed()
    {
        var template = ThreeAcross();
        using var gif = Image.Load<Rgba32>(Animate());

        Assert.Equal([0, 1, 2], SlotColours(gif, template)[0]);
    }

    // --- the file itself ------------------------------------------------------

    [Fact]
    public void There_is_one_frame_per_photo()
    {
        using var gif = Image.Load<Rgba32>(Animate());

        Assert.Equal(Palette.Length, gif.Frames.Count);
    }

    /// <summary>
    /// Shrunk to keep it downloadable over the booth's own wifi. 2000x600 at a
    /// 1000px limit is 1000x300.
    /// </summary>
    [Fact]
    public void The_animation_is_smaller_than_the_printed_strip()
    {
        using var gif = Image.Load<Rgba32>(Animate());

        Assert.Equal(1000, gif.Width);
        Assert.Equal(300, gif.Height);
    }

    [Fact]
    public void It_loops_for_ever()
    {
        using var gif = Image.Load<Rgba32>(Animate());

        // 0 is GIF's "repeat indefinitely".
        Assert.Equal(0, gif.Metadata.GetGifMetadata().RepeatCount);
    }

    /// <summary>
    /// GIF counts delay in hundredths of a second, so the requested milliseconds
    /// have to survive that conversion.
    /// </summary>
    [Fact]
    public void Each_frame_is_held_for_the_requested_time()
    {
        using var gif = Image.Load<Rgba32>(
            Animate(new AnimationOptions(FrameDelayMilliseconds: 500)));

        foreach (var frame in gif.Frames)
        {
            Assert.Equal(50, frame.Metadata.GetGifMetadata().FrameDelay);
        }
    }

    /// <summary>
    /// A browser renders a zero delay however it likes, and some race through the
    /// loop. Anything absurdly fast is clamped rather than passed through.
    /// </summary>
    [Fact]
    public void An_impossibly_short_delay_is_clamped_rather_than_left_at_zero()
    {
        using var gif = Image.Load<Rgba32>(
            Animate(new AnimationOptions(FrameDelayMilliseconds: 1)));

        Assert.True(gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay > 0);
    }

    // --- shrinking ------------------------------------------------------------

    [Theory]
    [InlineData(600, 1800, 333, 1000)]   // the classic 2x6 strip
    [InlineData(1800, 1200, 1000, 667)]  // a 6x4 landscape
    [InlineData(800, 400, 800, 400)]     // already small: left alone, never enlarged
    public void Shrinking_keeps_the_shape(int w, int h, int expectedW, int expectedH)
    {
        var shrunk = StripCompositor.Shrink(new TemplateCanvas(w, h), 1000);

        Assert.Equal(expectedW, shrunk.Width);
        Assert.Equal(expectedH, shrunk.Height);
    }
}
