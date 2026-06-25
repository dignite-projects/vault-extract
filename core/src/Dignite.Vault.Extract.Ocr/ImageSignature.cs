using System;

namespace Dignite.Vault.Extract.Ocr;

/// <summary>
/// Content-based detection of image / PDF formats by their leading magic bytes. Lives in the OCR
/// contract layer (the common floor referenced by every OCR provider and every Markdown extractor) so the
/// signature table is defined <b>once</b> rather than re-implemented per provider with divergent coverage.
/// Magic bytes are authoritative: a Content-Type / file extension is a caller-supplied hint and must not
/// override what the bytes actually are.
/// </summary>
public static class ImageSignature
{
    /// <summary>
    /// Returns the raster image media type the bytes carry (<c>image/png</c>, <c>image/jpeg</c>,
    /// <c>image/gif</c>, <c>image/bmp</c>, <c>image/webp</c>, <c>image/tiff</c>), or <c>null</c> when the
    /// bytes match no recognized raster signature (vector EMF/WMF, an unknown/exotic codec such as
    /// HEIC/AVIF, or corrupt/truncated data).
    /// </summary>
    // Signature prefixes as ReadOnlySpan<byte> properties: each compiles to a span over a static data
    // section, so the comparisons below allocate nothing per call (unlike a `params byte[]`).
    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> Gif => [0x47, 0x49, 0x46, 0x38]; // "GIF8"
    private static ReadOnlySpan<byte> Bmp => [0x42, 0x4D];             // "BM"
    private static ReadOnlySpan<byte> Riff => [0x52, 0x49, 0x46, 0x46]; // "RIFF"
    private static ReadOnlySpan<byte> Webp => [0x57, 0x45, 0x42, 0x50]; // "WEBP"
    private static ReadOnlySpan<byte> TiffLe => [0x49, 0x49, 0x2A, 0x00]; // "II*\0"
    private static ReadOnlySpan<byte> TiffBe => [0x4D, 0x4D, 0x00, 0x2A]; // "MM\0*"
    private static ReadOnlySpan<byte> Pdf => [0x25, 0x50, 0x44, 0x46, 0x2D]; // "%PDF-"

    public static string? SniffMediaType(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith(Jpeg))
        {
            return "image/jpeg";
        }

        if (b.StartsWith(Png))
        {
            return "image/png";
        }

        if (b.StartsWith(Gif))
        {
            return "image/gif";
        }

        if (b.StartsWith(Bmp))
        {
            return "image/bmp";
        }

        // "RIFF" .... "WEBP"
        if (b.StartsWith(Riff) && b.Length >= 12 && b.Slice(8, 4).SequenceEqual(Webp))
        {
            return "image/webp";
        }

        if (b.StartsWith(TiffLe) || b.StartsWith(TiffBe))
        {
            return "image/tiff";
        }

        return null;
    }

    /// <summary>Whether the bytes carry a recognized raster image signature.</summary>
    public static bool IsRaster(ReadOnlySpan<byte> bytes) => SniffMediaType(bytes) is not null;

    /// <summary>Whether the bytes begin with the PDF signature (<c>%PDF-</c>).</summary>
    public static bool IsPdf(ReadOnlySpan<byte> b) => b.StartsWith(Pdf);
}
