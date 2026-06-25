using System;
using NSubstitute;
using Shouldly;
using UglyToad.PdfPig.Content;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

public class PdfImagePayload_Tests
{
    [Fact]
    public void Returns_png_when_TryGetPng_succeeds()
    {
        var png = TinyPng.CreateSolid(8, 8);
        var image = Substitute.For<IPdfImage>();
        image.TryGetPng(out Arg.Any<byte[]>()!).Returns(ci =>
        {
            ci[0] = png;
            return true;
        });

        var result = PdfImagePayload.TryResolve(image);

        result.ShouldNotBeNull();
        result!.Value.ContentType.ShouldBe("image/png");
        result.Value.Bytes.ShouldBe(png);
    }

    [Fact]
    public void Returns_jpeg_from_raw_bytes_when_png_is_unavailable()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        var image = Substitute.For<IPdfImage>();
        image.TryGetPng(out Arg.Any<byte[]>()!).Returns(ci =>
        {
            ci[0] = Array.Empty<byte>();
            return false;
        });
        image.TryGetBytesAsMemory(out Arg.Any<Memory<byte>>()).Returns(ci =>
        {
            ci[0] = Memory<byte>.Empty;
            return false;
        });
        image.RawMemory.Returns(new Memory<byte>(jpeg));

        var result = PdfImagePayload.TryResolve(image);

        result.ShouldNotBeNull();
        result!.Value.ContentType.ShouldBe("image/jpeg");
        // The selected byte payload is exactly what gets fed to OCR — pin it, not just the content type.
        result.Value.Bytes.ShouldBe(jpeg);
    }

    [Fact]
    public void Returns_null_for_an_unsupported_codec()
    {
        var image = Substitute.For<IPdfImage>();
        image.TryGetPng(out Arg.Any<byte[]>()!).Returns(ci =>
        {
            ci[0] = Array.Empty<byte>();
            return false;
        });
        image.TryGetBytesAsMemory(out Arg.Any<Memory<byte>>()).Returns(ci =>
        {
            ci[0] = Memory<byte>.Empty;
            return false;
        });
        // Bytes with no recognizable JPEG/PNG signature (e.g. JBIG2 / JPX / CCITT or raw samples).
        image.RawMemory.Returns(new Memory<byte>(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }));

        PdfImagePayload.TryResolve(image).ShouldBeNull();
    }
}
