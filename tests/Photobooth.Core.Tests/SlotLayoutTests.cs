using Photobooth.Core;

namespace Photobooth.Core.Tests;

public class SlotLayoutTests
{
    private static readonly TemplateCanvas Strip2x6 = new(600, 1800, 300);
    private static readonly TemplateCanvas Landscape6x4 = new(1800, 1200, 300);

    [Fact]
    public void Orientation_comes_from_the_canvas_shape()
    {
        Assert.Equal(TemplateOrientation.Portrait, SlotLayout.OrientationOf(Strip2x6));
        Assert.Equal(TemplateOrientation.Landscape, SlotLayout.OrientationOf(Landscape6x4));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 4, 1)]
    // Past four a single column leaves slivers, so photos pair up.
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    public void A_portrait_strip_stacks_into_a_column(int photos, int rows, int columns)
    {
        Assert.Equal((rows, columns), SlotLayout.Grid(photos, TemplateOrientation.Portrait));
    }

    [Theory]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 3)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 3)]
    [InlineData(6, 2, 3)]
    public void Landscape_runs_along_a_row_then_becomes_a_grid(int photos, int rows, int columns)
    {
        Assert.Equal((rows, columns), SlotLayout.Grid(photos, TemplateOrientation.Landscape));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Every_slot_lands_inside_the_canvas(int photos)
    {
        foreach (var canvas in new[] { Strip2x6, Landscape6x4 })
        {
            foreach (var slot in SlotLayout.Arrange(photos, canvas))
            {
                Assert.InRange(slot.X, 0, 1);
                Assert.InRange(slot.Y, 0, 1);
                Assert.InRange(slot.X + slot.W, 0, 1.0001);
                Assert.InRange(slot.Y + slot.H, 0, 1.0001);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void Slots_never_overlap(int photos)
    {
        var slots = SlotLayout.Arrange(photos, Landscape6x4);

        for (var i = 0; i < slots.Count; i++)
        {
            for (var j = i + 1; j < slots.Count; j++)
            {
                Assert.False(Overlaps(slots[i], slots[j]),
                    $"slot {i} overlaps slot {j} with {photos} photos");
            }
        }
    }

    [Fact]
    public void Photos_are_the_same_size_as_each_other()
    {
        // "Evenly" is the whole point: a strip where one frame is larger than the
        // rest looks like a mistake.
        var slots = SlotLayout.Arrange(4, Strip2x6);

        Assert.All(slots, s => Assert.Equal(slots[0].W, s.W, 6));
        Assert.All(slots, s => Assert.Equal(slots[0].H, s.H, 6));
    }

    [Fact]
    public void Gaps_between_photos_are_equal()
    {
        var slots = SlotLayout.Arrange(4, Strip2x6);

        var gaps = slots.Zip(slots.Skip(1), (a, b) => b.Y - (a.Y + a.H)).ToList();

        Assert.All(gaps, g => Assert.Equal(gaps[0], g, 6));
        Assert.All(gaps, g => Assert.True(g > 0, "photos are touching"));
    }

    [Fact]
    public void The_footer_band_is_left_clear()
    {
        // The branding strip at the foot is why slots are not simply the canvas
        // divided by the photo count.
        var options = LayoutOptions.For(TemplateOrientation.Portrait);
        var slots = SlotLayout.Arrange(3, Strip2x6, options);

        var lowest = slots.Max(s => s.Y + s.H);
        Assert.True(lowest <= 1 - options.Footer + 0.001,
            $"a photo reaches {lowest:F3}, into the footer that starts at {1 - options.Footer:F3}");
    }

    [Fact]
    public void A_short_final_row_is_centred()
    {
        // Five photos in a 2x3 grid should look deliberate, not truncated.
        var slots = SlotLayout.Arrange(5, Landscape6x4);
        Assert.Equal(5, slots.Count);

        var lastRow = slots.Skip(3).ToList();
        Assert.Equal(2, lastRow.Count);

        var leftGap = lastRow[0].X;
        var rightGap = 1 - (lastRow[^1].X + lastRow[^1].W);
        Assert.Equal(leftGap, rightGap, 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void The_requested_number_of_photos_is_what_you_get(int photos)
    {
        Assert.Equal(photos, SlotLayout.Arrange(photos, Strip2x6).Count);
    }

    [Fact]
    public void Counts_outside_the_supported_range_are_clamped_rather_than_throwing()
    {
        // A stored template or a bad request should degrade, not crash the booth.
        Assert.Single(SlotLayout.Arrange(0, Strip2x6));
        Assert.Equal(SlotLayout.MaxPhotos, SlotLayout.Arrange(99, Strip2x6).Count);
    }

    [Fact]
    public void Absurd_spacing_yields_a_usable_slot_rather_than_a_negative_one()
    {
        // Margins that swallow the canvas must not produce negative sizes, which
        // would fail deep inside the compositor instead of here.
        var slots = SlotLayout.Arrange(
            3, Strip2x6, new LayoutOptions(Margin: 0.9, Gap: 0.5, Footer: 0.9));

        Assert.All(slots, s => Assert.True(s.W > 0 && s.H > 0));
    }

    private static bool Overlaps(TemplateSlot a, TemplateSlot b)
    {
        const double tolerance = 1e-9;
        return a.X + a.W > b.X + tolerance
            && b.X + b.W > a.X + tolerance
            && a.Y + a.H > b.Y + tolerance
            && b.Y + b.H > a.Y + tolerance;
    }
}
