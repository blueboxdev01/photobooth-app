namespace Photobooth.Core;

/// <summary>A named output size, so nobody has to remember that 2×6 at 300 DPI is 600×1800.</summary>
public sealed record CanvasPreset(string Id, string Label, TemplateCanvas Canvas)
{
    public TemplateOrientation Orientation => SlotLayout.OrientationOf(Canvas);

    public string Inches =>
        $"{Canvas.WidthInches:0.#}×{Canvas.HeightInches:0.#} in";
}

/// <summary>
/// The output sizes the booth offers.
///
/// All at 300 DPI, which is what photo printing expects and what the strip's
/// JFIF header claims. Sizes are the common print formats rather than arbitrary
/// pixel dimensions, because the whole point of a template is that it comes out
/// of a printer the right physical size.
/// </summary>
public static class CanvasPresets
{
    public const string DefaultId = "strip-2x6";

    public static IReadOnlyList<CanvasPreset> All { get; } =
    [
        new("strip-2x6", "Photo strip 2×6", new TemplateCanvas(600, 1800)),
        new("portrait-4x6", "Portrait 4×6", new TemplateCanvas(1200, 1800)),
        new("portrait-5x7", "Portrait 5×7", new TemplateCanvas(1500, 2100)),
        new("landscape-6x4", "Landscape 6×4", new TemplateCanvas(1800, 1200)),
        new("landscape-7x5", "Landscape 7×5", new TemplateCanvas(2100, 1500)),
    ];

    public static CanvasPreset Default => Find(DefaultId)!;

    public static CanvasPreset? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The preset matching a canvas exactly, if there is one. Lets the UI show
    /// "Photo strip 2×6" rather than raw pixels for a size it recognises, while
    /// still allowing hand-edited dimensions.
    /// </summary>
    public static CanvasPreset? Matching(TemplateCanvas canvas) =>
        All.FirstOrDefault(p =>
            p.Canvas.Width == canvas.Width
            && p.Canvas.Height == canvas.Height
            && p.Canvas.Dpi == canvas.Dpi);
}
