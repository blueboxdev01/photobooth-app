namespace Photobooth.Core;

/// <summary>Output size in pixels, plus the DPI stamped into the file.</summary>
public sealed record TemplateCanvas(int Width, int Height, int Dpi = 300)
{
    public double WidthInches => Width / (double)Dpi;
    public double HeightInches => Height / (double)Dpi;
}

public enum SlotFit
{
    /// <summary>Fill the slot, cropping whatever does not fit. The booth default.</summary>
    Cover,

    /// <summary>Fit the whole photo inside the slot, leaving empty space.</summary>
    Contain,
}

/// <summary>
/// Where one photo goes, in fractions of the canvas rather than pixels.
///
/// Normalised on purpose: a template survives a change of output size or DPI, and
/// the visual editor in M8 becomes a drag-rectangle over a preview rather than
/// something that has to recompute pixels.
/// </summary>
public sealed record TemplateSlot(double X, double Y, double W, double H, SlotFit Fit = SlotFit.Cover)
{
    public (int X, int Y, int W, int H) ToPixels(TemplateCanvas canvas) => (
        (int)Math.Round(X * canvas.Width),
        (int)Math.Round(Y * canvas.Height),
        (int)Math.Round(W * canvas.Width),
        (int)Math.Round(H * canvas.Height));
}

/// <summary>
/// A strip layout: the canvas, where the photos go, and optional frame art drawn
/// over the top.
///
/// The slot count is the number of photos a session takes. Deriving it here
/// rather than from a separate setting means a three-frame strip cannot end up
/// paired with a four-shot session.
/// </summary>
public sealed record StripTemplate(
    string Name,
    TemplateCanvas Canvas,
    IReadOnlyList<TemplateSlot> Slots,
    string? Overlay = null,
    string Background = "#FFFFFF")
{
    public int ShotCount => Slots.Count;
}

/// <summary>Supplies the template in force. Implemented in Photobooth.Imaging.</summary>
public interface ITemplateProvider
{
    StripTemplate Current { get; }
}
