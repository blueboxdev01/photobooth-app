namespace Photobooth.Core;

/// <summary>
/// A photo that has landed on disk and been verified as completely written.
/// </summary>
/// <param name="FilePath">Absolute path to the file, still in the watch folder.</param>
/// <param name="FileName">File name as the camera software wrote it, e.g. IMG_0042.JPG.</param>
/// <param name="SizeBytes">Size at the moment the file was judged complete.</param>
/// <param name="DetectedAtUtc">When ingest accepted the file, not when the shutter fired.</param>
public sealed record CapturedPhoto(
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset DetectedAtUtc);
