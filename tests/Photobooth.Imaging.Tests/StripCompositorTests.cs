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
