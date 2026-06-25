using Shouldly;
using Xunit;
using Dignite.Vault.Extract.Ocr;

namespace Dignite.Vault.Extract.Ocr;

/// <summary>
/// Unit tests for the shared magic-byte detector (consolidated from the three former per-provider
/// sniffers). Positive cases for every supported raster signature, plus the PDF signature and the
/// no-match path.
/// </summary>
public class ImageSignature_Tests
{
    [Theory]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 })]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData("image/bmp", new byte[] { 0x42, 0x4D, 0x00, 0x00 })]
    [InlineData("image/tiff", new byte[] { 0x49, 0x49, 0x2A, 0x00 })]
    [InlineData("image/tiff", new byte[] { 0x4D, 0x4D, 0x00, 0x2A })]
    public void Sniffs_each_supported_raster_signature(string expected, byte[] bytes)
    {
        ImageSignature.SniffMediaType(bytes).ShouldBe(expected);
        ImageSignature.IsRaster(bytes).ShouldBeTrue();
    }

    [Fact]
    public void Sniffs_webp_only_with_the_RIFF_and_WEBP_markers()
    {
        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 };
        ImageSignature.SniffMediaType(webp).ShouldBe("image/webp");

        // RIFF but not WEBP (e.g. a WAV) is not a raster image.
        var riffNotWebp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 };
        ImageSignature.SniffMediaType(riffNotWebp).ShouldBeNull();
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 })]   // garbage
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x00 })]          // EMF-ish (vector) — not a raster
    [InlineData(new byte[] { })]                                  // empty
    [InlineData(new byte[] { 0xFF, 0xD8 })]                       // truncated JPEG prefix (too short)
    public void Returns_null_for_non_raster_or_truncated_bytes(byte[] bytes)
    {
        ImageSignature.SniffMediaType(bytes).ShouldBeNull();
        ImageSignature.IsRaster(bytes).ShouldBeFalse();
    }

    [Fact]
    public void Detects_the_pdf_signature()
    {
        ImageSignature.IsPdf(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }).ShouldBeTrue(); // %PDF-1
        ImageSignature.IsPdf(new byte[] { 0x89, 0x50, 0x4E, 0x47 }).ShouldBeFalse();
    }
}
