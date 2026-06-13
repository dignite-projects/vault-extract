using System.IO;
using Dignite.DocumentAI.Ocr;
using DocumentFormat.OpenXml.Packaging;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Resolves an embedded <see cref="ImagePart"/> into image-file bytes + MIME type suitable for feeding
/// to <c>IOcrProvider.RecognizeAsync</c>. Unlike the PDF path, PPTX stores each image as a self-contained
/// image file (PNG/JPEG/GIF/BMP/TIFF/WebP), so the bytes pass straight through with no re-encoding.
/// <para>
/// The media type is taken from the <b>bytes' own signature</b> (<see cref="ImageSignature.SniffMediaType"/>),
/// not the part's declared content type — a mislabeled part (e.g. an EMF renamed to <c>.png</c>) or a
/// truncated/corrupt stream therefore reports <see cref="ImageOutcome.Undecodable"/>, as do genuine vector
/// parts (EMF/WMF), per the #299 decision to skip cross-platform vector rasterization.
/// </para>
/// <para>
/// The part stream is read with a <b>hard byte cap</b> and aborts as soon as the cap is exceeded, so a
/// ZIP-decompression-bomb image part can never be fully inflated into managed memory — it reports
/// <see cref="ImageOutcome.Oversized"/> instead. The caller trips the completeness signal (#268) for both
/// non-OK outcomes.
/// </para>
/// </summary>
internal static class PptxImagePayload
{
    internal enum ImageOutcome
    {
        Ok,
        Undecodable,
        Oversized
    }

    internal readonly record struct ResolvedImage(ImageOutcome Outcome, byte[]? Bytes, string? ContentType);

    public static ResolvedImage TryResolve(ImagePart imagePart, long maxBytes)
    {
        using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);

        if (!TextExtractionStreams.TryReadAllBytesBounded(stream, maxBytes, out var bytes))
        {
            // Exceeded the cap mid-read — never materialized beyond the cap (the zip-bomb guard).
            return new ResolvedImage(ImageOutcome.Oversized, null, null);
        }

        var contentType = ImageSignature.SniffMediaType(bytes);
        return contentType is null
            ? new ResolvedImage(ImageOutcome.Undecodable, null, null)
            : new ResolvedImage(ImageOutcome.Ok, bytes, contentType);
    }
}
