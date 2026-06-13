using Dignite.DocumentAI.Ocr;
using UglyToad.PdfPig.Content;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Resolves an embedded <see cref="IPdfImage"/> into image-file bytes + MIME type suitable for feeding
/// to <c>IOcrProvider.RecognizeAsync</c>. Returns <c>null</c> for codecs PdfPig cannot turn into a
/// standalone image file (JBIG2 / JPX / CCITT, or raw bitmaps it cannot PNG-encode) — the caller treats
/// that as an undecodable image and trips the completeness signal.
/// </summary>
internal static class PdfImagePayload
{
    /// <returns>PNG/JPEG bytes + content type, or <c>null</c> if the image cannot be decoded to a file.</returns>
    public static (byte[] Bytes, string ContentType)? TryResolve(IPdfImage image)
    {
        // Preferred path: PdfPig reverses the PDF filters and re-encodes the bitmap as a valid PNG.
        // Covers Flate / LZW / raw-sample bitmaps (the common embedded-screenshot case).
        if (image.TryGetPng(out var png) && png is { Length: > 0 })
        {
            return (png, "image/png");
        }

        // PdfPig does not decode DCT (JPEG) without an external filter; for those, the raw stream IS a
        // valid image file. TryGetBytesAsMemory reverses non-DCT filters; otherwise use the raw bytes.
        var raw = image.TryGetBytesAsMemory(out var decoded) ? decoded : image.RawMemory;

        // Inspect the signature on the span first (via the shared Ocr-contract sniffer); only materialize a
        // byte[] when we will actually return it (an unsupported/headerless image otherwise pays a
        // full-buffer copy just to be discarded).
        var contentType = ImageSignature.SniffMediaType(raw.Span);
        if (contentType is not null)
        {
            return (raw.ToArray(), contentType);
        }

        // Unsupported codec (JBIG2 / JPX / CCITT) or undecoded raw samples without a file header.
        return null;
    }
}
