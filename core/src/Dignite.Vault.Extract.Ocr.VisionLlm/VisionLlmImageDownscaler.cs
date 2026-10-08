using System;
using SkiaSharp;

namespace Dignite.Vault.Extract.Ocr.VisionLlm;

public enum ImageDownscaleStatus
{
    /// <summary>Already small enough, or shrinking is switched off: send the image as it is.</summary>
    WithinBudget,

    /// <summary>Too large; <see cref="ImageDownscaleResult.Data"/> holds the smaller copy.</summary>
    Downscaled,

    /// <summary>Not an image SkiaSharp can read (HEIC, a corrupt file, ...): send it as it is.</summary>
    Unreadable
}

/// <param name="Status">What happened to the image.</param>
/// <param name="OriginalWidth">Width as the picture is meant to be seen, after any EXIF rotation. 0 when unreadable.</param>
/// <param name="OriginalHeight">Height as the picture is meant to be seen. 0 when unreadable.</param>
/// <param name="Data">The smaller image, upright and without EXIF; only when <see cref="ImageDownscaleStatus.Downscaled"/>.</param>
/// <param name="MediaType">Media type of <paramref name="Data"/>.</param>
/// <param name="Width">Width of <paramref name="Data"/>.</param>
/// <param name="Height">Height of <paramref name="Data"/>.</param>
public sealed record ImageDownscaleResult(
    ImageDownscaleStatus Status,
    int OriginalWidth = 0,
    int OriginalHeight = 0,
    byte[]? Data = null,
    string? MediaType = null,
    int Width = 0,
    int Height = 0);

/// <summary>
/// Shrinks a photo / screenshot / rasterized PDF page before it is sent to the vision model (#692).
/// <para>
/// Why: a vision model reads an image as a number of tokens that grows with its pixel count. On a large photo
/// of a form with big blank fields, Qwen3-VL-32B falls into writing empty table rows until it hits
/// <see cref="VisionLlmOcrOptions.MaxOutputTokens"/>; <see cref="VisionLlmOutputGuard"/> then (correctly)
/// discards the whole page and the document comes back with no text and no fields. Sampling settings
/// (temperature 0 / 0.7 + presence_penalty 1.5) made no difference; the pixel count did (see the issue).
/// </para>
/// <para>
/// An image within the budget is left untouched (no re-encoding), so what already worked keeps working.
/// A larger one is scaled down to at most <c>maxPixels</c>, keeping its aspect ratio; it is also rotated upright
/// according to its EXIF orientation, because the copy carries no EXIF and a photo taken sideways would
/// otherwise reach the model on its side.
/// </para>
/// </summary>
public static class VisionLlmImageDownscaler
{
    /// <summary>About 1.5 million pixels (1140 x 1315): a little under the largest size that read correctly.</summary>
    public const int DefaultMaxPixels = 1_500_000;

    private const int JpegQuality = 90;

    /// <param name="data">The encoded image.</param>
    /// <param name="maxPixels">The most pixels (width times height) to send. Zero or less switches shrinking off.</param>
    public static ImageDownscaleResult Downscale(ReadOnlyMemory<byte> data, int maxPixels)
    {
        if (maxPixels <= 0)
        {
            return new ImageDownscaleResult(ImageDownscaleStatus.WithinBudget);
        }

        using var encoded = SKData.CreateCopy(data.Span);
        using var codec = SKCodec.Create(encoded);
        if (codec is null)
        {
            return new ImageDownscaleResult(ImageDownscaleStatus.Unreadable);
        }

        var origin = codec.EncodedOrigin;
        var swapsAxes = SwapsAxes(origin);
        var seenWidth = swapsAxes ? codec.Info.Height : codec.Info.Width;
        var seenHeight = swapsAxes ? codec.Info.Width : codec.Info.Height;
        var pixels = (long)codec.Info.Width * codec.Info.Height;

        if (pixels <= maxPixels)
        {
            return new ImageDownscaleResult(ImageDownscaleStatus.WithinBudget, seenWidth, seenHeight);
        }

        using var source = SKBitmap.Decode(codec);
        if (source is null)
        {
            return new ImageDownscaleResult(ImageDownscaleStatus.Unreadable);
        }

        var scale = Math.Sqrt((double)maxPixels / pixels);
        var scaledWidth = Math.Max(1, (int)Math.Floor(source.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Floor(source.Height * scale));
        using var scaled = source.Resize(
            new SKImageInfo(scaledWidth, scaledHeight, source.ColorType, source.AlphaType),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null)
        {
            return new ImageDownscaleResult(ImageDownscaleStatus.Unreadable);
        }

        // A PNG (a screenshot, a rasterized PDF page) stays a PNG so the text stays sharp; everything else
        // becomes a JPEG.
        var asPng = codec.EncodedFormat == SKEncodedImageFormat.Png;
        using var upright = ApplyOrigin(scaled, origin, flattenOnWhite: !asPng);
        using var image = SKImage.FromBitmap(upright);
        using var smaller = image.Encode(asPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, JpegQuality);

        return new ImageDownscaleResult(
            ImageDownscaleStatus.Downscaled,
            seenWidth,
            seenHeight,
            smaller.ToArray(),
            asPng ? "image/png" : "image/jpeg",
            upright.Width,
            upright.Height);
    }

    /// <summary>
    /// Draws <paramref name="source"/> as a person holding the camera the right way up would see it, given the
    /// EXIF orientation it was stored with. <paramref name="flattenOnWhite"/> paints a white background first,
    /// for a format with no alpha channel (JPEG would turn transparent pixels black).
    /// </summary>
    public static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin, bool flattenOnWhite = false)
    {
        var width = source.Width;
        var height = source.Height;
        var swapsAxes = SwapsAxes(origin);

        var result = new SKBitmap(
            swapsAxes ? height : width,
            swapsAxes ? width : height,
            source.ColorType,
            source.AlphaType);

        using var canvas = new SKCanvas(result);
        if (flattenOnWhite)
        {
            canvas.Clear(SKColors.White);
        }

        // Each case maps a stored pixel (x, y) to where it belongs: x' = a*x + b*y + tx, y' = c*x + d*y + ty.
        // Orientation values 1 to 8 are the EXIF ones: 2 is mirrored, 3 upside down, 6 needs a quarter turn
        // clockwise, 8 a quarter turn counterclockwise, 5 and 7 are the mirrored quarter turns.
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => Matrix(-1, 0, width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => Matrix(-1, 0, width, 0, -1, height),
            SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, height),
            SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => Matrix(0, -1, height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => Matrix(0, -1, height, -1, 0, width),
            SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, width),
            _ => SKMatrix.Identity
        };

        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);

        return result;
    }

    private static bool SwapsAxes(SKEncodedOrigin origin)
    {
        return origin is SKEncodedOrigin.LeftTop
            or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
    }

    private static SKMatrix Matrix(float a, float b, float tx, float c, float d, float ty)
    {
        return new SKMatrix(a, b, tx, c, d, ty, 0, 0, 1);
    }
}
