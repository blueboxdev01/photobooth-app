using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Photobooth.Core;
using Photobooth.Imaging;

namespace Photobooth.Imaging.Tests;

public sealed class FileTemplateProviderTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "pb-templates", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private FileTemplateProvider Build(string selected = "classic-2x6") =>
        new(Options.Create(new TemplateOptions { Folder = _folder, Selected = selected }),
            NullLogger<FileTemplateProvider>.Instance);

    private static StripTemplate Template(int slots, string name = "Test") => new(
        name,
        new TemplateCanvas(600, 1800, 300),
        [.. Enumerable.Range(0, slots)
            .Select(i => new TemplateSlot(0.05, 0.02 + i * 0.25, 0.9, 0.22))]);

    [Theory]
    [InlineData("classic-2x6", true)]
    [InlineData("my_frame_2", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("../escape", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("has space", false)]
    [InlineData("-leading", false)]
    public void Template_names_that_could_escape_the_folder_are_rejected(string name, bool valid)
    {
        // Operators name templates and those names become file paths, so this is
        // the only thing between a typed name and a write outside the folder.
        Assert.Equal(valid, FileTemplateProvider.IsValidName(name));
    }

    [Fact]
    public void Saving_then_loading_round_trips_the_layout()
    {
        var provider = Build();
        var original = Template(4, "Grid 4-up");

        provider.Save("grid-4up", original);

        var reloaded = Build("grid-4up").Current;
        Assert.Equal(original.Name, reloaded.Name);
        Assert.Equal(4, reloaded.ShotCount);
        Assert.Equal(original.Canvas, reloaded.Canvas);
        Assert.Equal(original.Slots.Count, reloaded.Slots.Count);
        Assert.Equal(original.Slots[2].Y, reloaded.Slots[2].Y, 4);
    }

    [Fact]
    public void Saving_makes_the_template_current()
    {
        // Saving a four-slot template must change how many photos a session takes,
        // with nothing else to remember to update.
        var provider = Build();

        provider.Save("grid-4up", Template(4));

        Assert.Equal(4, provider.Current.ShotCount);
        Assert.False(provider.UsingFallback);
    }

    [Fact]
    public void An_invalid_name_is_refused_rather_than_written()
    {
        var provider = Build();

        Assert.Throws<ArgumentException>(() => provider.Save("../escape", Template(3)));

        // Nothing written anywhere -- including the parent, which is what a
        // traversing name would have reached.
        var parent = Directory.GetParent(_folder)!.FullName;
        Assert.Empty(Directory.EnumerateFiles(parent, "escape*", SearchOption.TopDirectoryOnly));
        Assert.False(Directory.Exists(_folder) &&
                     Directory.EnumerateFiles(_folder, "*.json").Any());
    }

    [Fact]
    public void A_missing_template_falls_back_and_says_so()
    {
        // The fallback shares the real template's name and dimensions, so without
        // this flag a missing file looks completely fine except for absent frame
        // art -- which is exactly how it went unnoticed once already.
        var provider = Build("does-not-exist");

        var template = provider.Current;

        Assert.Equal(3, template.ShotCount);
        Assert.True(provider.UsingFallback);
        Assert.Contains("built-in fallback", provider.Source);
    }

    [Fact]
    public void Unparseable_json_falls_back_rather_than_failing_to_start()
    {
        // A booth that cannot read its template should still take photos.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ not json");

        var provider = Build("broken");

        Assert.True(provider.UsingFallback);
        Assert.Contains("could not be read", provider.Source);
    }

    [Fact]
    public void A_template_with_no_slots_falls_back()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "empty.json"),
            """{"name":"Empty","canvas":{"width":600,"height":1800,"dpi":300},"slots":[]}""");

        var provider = Build("empty");

        Assert.True(provider.UsingFallback);
    }

    [Fact]
    public void Frame_art_must_be_a_png()
    {
        // A JPEG has no transparency, so it would paint solidly over every photo.
        var provider = Build();
        var jpegMagic = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0 };

        var ex = Assert.Throws<ArgumentException>(() => provider.SaveOverlay("frame", jpegMagic));

        Assert.Contains("PNG", ex.Message);
        Assert.Null(provider.OverlayPath("frame"));
    }

    [Fact]
    public void A_real_png_is_accepted_and_findable()
    {
        var provider = Build();
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 0, 0, 0, 0, 0,
        };

        var saved = provider.SaveOverlay("frame", png);

        Assert.Equal("frame.png", saved);
        Assert.NotNull(provider.OverlayPath("frame"));
    }

    [Fact]
    public void Available_lists_what_can_be_chosen()
    {
        var provider = Build();
        provider.Save("one", Template(3));
        provider.Save("two", Template(4));

        Assert.Equal(["one", "two"], provider.Available());
    }
}
