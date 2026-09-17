using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Photobooth.Core;
using Photobooth.Imaging;
using SkiaSharp;

namespace Photobooth.Imaging.Tests;

public sealed class StripCompositorTests : IDisposable
{
    private readonly string _output =
        Path.Combine(Path.GetTempPath(), "pb-imaging", Guid.NewGuid().ToString("N"));

    private static string TemplateFolder => Path.Combine(AppContext.BaseDirectory, "templates");
    private static string SampleFolder => Path.Combine(AppContext.BaseDirectory, "samples");
    private static string GoldenFolder => Path.Combine(AppContext.BaseDirectory, "golden");

    private readonly StripCompositor _compositor =
        new(NullLogger<StripCompositor>.Instance);

    public StripCompositorTests() => Directory.CreateDirectory(_output);

    public void Dispose()
    {
        try { Directory.Delete(_output, recursive: true); } catch { /* best effort */ }
    }

    private static StripTemplate Classic()
    {
        var provider = new FileTemplateProvider(
            Options.Create(new TemplateOptions { Folder = TemplateFolder }),
            NullLogger<FileTemplateProvider>.Instance);
        return provider.Current;
    }

    private static List<string> Photos(int count) =>
        Directory.EnumerateFiles(SampleFolder, "*.jpg")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();

    private string Compose(StripTemplate template, int photoCount)
    {
        var path = Path.Combine(_output, "strip.jpg");
        _compositor.Compose(template, Photos(photoCount), TemplateFolder, path);
        return path;
    }

    [Fact]
    public void The_shipped_template_is_a_three_slot_2x6_at_300_dpi()
    {
        var template = Classic();

        Assert.Equal(3, template.ShotCount);
        Assert.Equal(600, template.Canvas.Width);
        Assert.Equal(1800, template.Canvas.Height);
        Assert.Equal(300, template.Canvas.Dpi);
        Assert.Equal(2, template.Canvas.WidthInches, 3);
        Assert.Equal(6, template.Canvas.HeightInches, 3);
    }

    [Fact]
    public void Output_is_exactly_the_canvas_size()
    {
        var template = Classic();

        var path = Compose(template, template.ShotCount);

        using var bitmap = SKBitmap.Decode(path);
        Assert.Equal(template.Canvas.Width, bitmap.Width);
        Assert.Equal(template.Canvas.Height, bitmap.Height);
    }

    [Fact]
    public void Output_carries_its_physical_size()
    {
        // Without this the file is just pixels, and a print dialog guesses at the
        // physical size -- which is how a 2x6 strip comes out the wrong size.
        var template = Classic();

        var path = Compose(template, template.ShotCount);

        var density = JpegDensity.Read(path);
        Assert.NotNull(density);
        Assert.Equal(300, density!.Value.X);
        Assert.Equal(300, density.Value.Y);
    }

    [Fact]
    public void Refuses_a_photo_count_that_does_not_match_the_slots()
    {
        // The template decides the shot count, so this should be unreachable --
        // which is exactly why it must throw rather than quietly half-fill a strip.
        var template = Classic();

        var ex = Assert.Throws<ArgumentException>(() =>
            _compositor.Compose(
                template, Photos(2), TemplateFolder, Path.Combine(_output, "bad.jpg")));

        Assert.Contains("3 slots", ex.Message);
        Assert.Contains("2 photos", ex.Message);
    }

    [Theory]
    // A 3:2 photo into a 4:3 slot: the sides are trimmed, full height kept.
    [InlineData(6000, 4000, 4f / 3f, 5333, 4000)]
    // A 4:3 photo into a 4:3 slot: nothing is trimmed.
    [InlineData(4000, 3000, 4f / 3f, 4000, 3000)]
    // A square photo into a wide slot: the top and bottom go instead.
    [InlineData(3000, 3000, 3f / 2f, 3000, 2000)]
    public void Cover_crop_takes_the_centre_at_the_slot_aspect(
        int width, int height, float aspect, int expectedWidth, int expectedHeight)
    {
        var crop = StripCompositor.CoverCrop(width, height, aspect);

        Assert.Equal(expectedWidth, crop.Width, 0);
        Assert.Equal(expectedHeight, crop.Height, 0);

        // Centred: equal trim on both sides.
        Assert.Equal(crop.Left, width - crop.Right, 1);
        Assert.Equal(crop.Top, height - crop.Bottom, 1);
    }

    [Fact]
    public void How_much_of_a_3_to_2_photo_survives_a_4_to_3_slot()
    {
        // Documents the cost of the layout choice: three 3:2 frames leave a dead
        // band at the foot of the strip, so the slots are 4:3 -- and this is what
        // that takes off the sides of every guest.
        var crop = StripCompositor.CoverCrop(6000, 4000, 4f / 3f);

        var kept = crop.Width / 6000f;
        Assert.InRange(kept, 0.88f, 0.90f);   // ~89% of the frame width survives
    }

    /// <summary>
    /// A fine checkerboard shrunk into a slot must come out grey.
    ///
    /// <para>
    /// This is the test that was missing. Every other test here asserts geometry
    /// -- size, DPI, slot placement, a golden image -- and all of them passed
    /// happily while photos were being downscaled with nearest-neighbour
    /// sampling, which threw away about eighty-nine pixels in ninety and made
    /// every strip look soft beside the originals it was built from.
    /// </para>
    ///
    /// <para>
    /// The source is a one-pixel checkerboard reduced exactly tenfold. Filter it
    /// properly and equal amounts of black and white average into flat mid-grey.
    /// Point-sample it and every tap lands on the same parity of the checker, so
    /// the slot comes out <b>solid black</b> -- far outside the band asserted
    /// below.
    /// </para>
    /// </summary>
    [Fact]
    public void A_fine_pattern_shrunk_into_a_slot_is_averaged_not_point_sampled()
    {
        // A small synthetic template rather than the shipped one. The claim is
        // about the resampler, not about any particular layout, and keeping the
        // canvas tiny keeps the source that feeds it tiny too -- a tenfold
        // reduction of a real 2x6 slot would mean generating 23 megapixels of
        // checkerboard for every run.
        var template = new StripTemplate(
            "checker", new TemplateCanvas(200, 150), [new TemplateSlot(0.1, 0.1, 0.8, 0.8)]);

        var (sx, sy, sw, sh) = template.Slots[0].ToPixels(template.Canvas);

        // Exactly ten source pixels per output pixel, at the slot's own aspect so
        // CoverCrop takes nothing off and the ratio stays exact. An even ratio
        // makes nearest-neighbour deterministic rather than moire, which is what
        // lets the assertion below be a tight one.
        var checker = Checkerboard(sw * 10, sh * 10);
        var photos = Enumerable.Repeat(checker, template.Slots.Count).ToList();

        var path = Path.Combine(_output, "checker-strip.jpg");
        _compositor.Compose(template, photos, TemplateFolder, path);

        using var strip = SKBitmap.Decode(path);
        var (mean, deviation) = Statistics(strip, sx, sy, sw, sh);

        // The claim that catches point sampling. Averaged, this lands near 128;
        // point-sampled it is 0. The band is wide on purpose -- the exact value
        // depends on whether the resampler averages in sRGB or linear light, and
        // that is not what this test is about.
        Assert.True(mean is > 60 and < 210,
            $"slot averaged {mean:F1}; a filtered tenfold reduction of a "
            + "black-and-white checkerboard should be mid-grey, not an extreme. "
            + "A value near 0 or 255 means every sample hit one phase of the "
            + "pattern -- nearest-neighbour sampling.");

        // And it must be *flat*. This one guards the other failure mode: a
        // half-filtered reduction that averages out correctly overall while
        // showing moire banding across the slot.
        Assert.True(deviation < 12,
            $"slot varies by {deviation:F1} levels; a reduced checkerboard "
            + "should be uniform, so this is moire from undersampling.");
    }

    /// <summary>A one-pixel black and white checkerboard, written as PNG.</summary>
    private string Checkerboard(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, ((x + y) & 1) == 0 ? SKColors.Black : SKColors.White);
            }
        }

        // PNG, not JPEG: a one-pixel checkerboard is the worst case for JPEG and
        // the source has to be exact for the assertion to mean anything.
        var path = Path.Combine(_output, $"checker-{width}x{height}.png");
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }

    /// <summary>
    /// Mean and standard deviation of the grey level inside a slot, inset from
    /// its edges so JPEG ringing at the boundary does not pollute the figures.
    /// </summary>
    private static (double Mean, double Deviation) Statistics(
        SKBitmap bitmap, int x, int y, int w, int h)
    {
        var insetX = x + (w / 6);
        var insetY = y + (h / 6);
        var right = x + w - (w / 6);
        var bottom = y + h - (h / 6);

        var values = new List<double>();
        for (var py = insetY; py < bottom; py++)
        {
            for (var px = insetX; px < right; px++)
            {
                var p = bitmap.GetPixel(px, py);
                values.Add((p.Red + p.Green + p.Blue) / 3.0);
            }
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, Math.Sqrt(variance));
    }

    [Fact]
    public void The_strip_matches_the_golden_image()
    {
        var template = Classic();
        var path = Compose(template, template.ShotCount);
        var golden = Path.Combine(GoldenFolder, "classic-2x6.png");

        if (Environment.GetEnvironmentVariable("PHOTOBOOTH_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenFolder);
            using var produced = SKBitmap.Decode(path);
            using var data = produced.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(golden);
            data.SaveTo(file);
            Assert.Fail($"Golden refreshed at {golden}. Copy it into the repo and re-run.");
        }

        Assert.True(File.Exists(golden),
            $"No golden image at {golden}. Re-run with PHOTOBOOTH_UPDATE_GOLDEN=1 to create one.");

        using var expected = SKBitmap.Decode(golden);
        using var actual = SKBitmap.Decode(path);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        // Compared with a tolerance rather than byte-for-byte: JPEG encoding and
        // Skia's resampling vary slightly between versions, and a test that breaks
        // on a library bump while the layout is unchanged teaches people to ignore
        // it. A moved slot shifts thousands of pixels and still fails loudly.
        var difference = MeanAbsoluteDifference(expected, actual);
        Assert.True(difference < 2.0,
            $"strip differs from the golden by {difference:F2} levels per channel");
    }

    private static double MeanAbsoluteDifference(SKBitmap a, SKBitmap b)
    {
        double total = 0;
        var samples = 0;

        // Every 4th pixel: enough to catch a layout change, fast enough to keep
        // the suite quick on a 600x1800 canvas.
        for (var y = 0; y < a.Height; y += 4)
        {
            for (var x = 0; x < a.Width; x += 4)
            {
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(x, y);
                total += Math.Abs(p.Red - q.Red)
                       + Math.Abs(p.Green - q.Green)
                       + Math.Abs(p.Blue - q.Blue);
                samples += 3;
            }
        }

        return samples == 0 ? 0 : total / samples;
    }
}
