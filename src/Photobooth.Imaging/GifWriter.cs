using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace Photobooth.Imaging;

/// <summary>
/// Writes rendered frames out as one looping GIF.
///
/// <para>
/// GIF rather than the technically better animated WebP, which SkiaSharp could
/// produce with no dependency at all. The deciding factor is not quality: a
/// guest saves this to their camera roll and sends it to someone. iOS commonly
/// saves an animated WebP as a still, and messaging apps strip the animation.
/// A GIF plays everywhere, which is the entire point of making one.
/// </para>
///
/// <para>
/// ImageSharp is used <b>only as an encoder</b>. It is handed raw pixels that
/// this app rendered a moment earlier and is never pointed at a file from a
/// camera, a guest or an operator, so it is not parsing anything hostile.
/// </para>
/// </summary>
internal static class GifWriter
{
    /// <summary>
    /// GIF stores frame delay in hundredths of a second, so anything faster than
    /// 10ms cannot be expressed and a zero delay means "as fast as possible",
    /// which browsers each interpret differently.
    /// </summary>
    private const int MinimumDelayHundredths = 2;

    /// <summary>
    /// Write the frames, in order, as an infinitely looping GIF.
    /// </summary>
    /// <param name="frames">
    /// Frames in <see cref="SKColorType.Rgba8888"/>, all the same size. The
    /// caller guarantees the layout, because reading them in the wrong byte
    /// order produces a file that is perfectly valid and has the red and blue
    /// channels swapped.
    /// </param>
    public static void Write(
        IReadOnlyList<SKBitmap> frames, string outputPath, int frameDelayMilliseconds)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
        }

        var delay = Math.Max(MinimumDelayHundredths, frameDelayMilliseconds / 10);

        Image<Rgba32>? animation = null;

        try
        {
            foreach (var frame in frames)
            {
                using var converted = ToImage(frame);

                if (animation is null)
                {
                    animation = converted.Clone();
                    Delay(animation.Frames.RootFrame, delay);
                    continue;
                }

                // AddFrame copies the frame in, so the source can be disposed
                // straight after.
                var added = animation.Frames.AddFrame(converted.Frames.RootFrame);
                Delay(added, delay);
            }

            // 0 means loop for ever, which is what every photobooth GIF does.
            animation!.Metadata.GetGifMetadata().RepeatCount = 0;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            animation.SaveAsGif(outputPath, new GifEncoder());
        }
        finally
        {
            animation?.Dispose();
        }
    }

    private static void Delay(ImageFrame<Rgba32> frame, int hundredths) =>
        frame.Metadata.GetGifMetadata().FrameDelay = hundredths;

    /// <summary>
    /// Hand the pixels across without either library decoding anything.
    /// </summary>
    private static Image<Rgba32> ToImage(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888)
        {
            // Worth refusing rather than rendering swapped colours: BGRA is the
            // platform default on Windows, so this is a mistake waiting to be
            // made, and its symptom is a GIF that looks fine except that
            // everyone is blue.
            throw new ArgumentException(
                $"Frames must be Rgba8888; this one is {bitmap.ColorType}.", nameof(bitmap));
        }

        return Image.LoadPixelData<Rgba32>(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height);
    }
}
