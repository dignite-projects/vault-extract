using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Unit tests for how <see cref="DefaultTextExtractor"/> maps the OCR provider's flat native-payload fields
/// (#210) to <c>TextExtractionResult.NativePayload</c>. The orchestrator owns this cross-contract mapping
/// (the Ocr project does not reference Abstractions), including the "half-filled → drop" guard that the
/// integration tests do not exercise.
/// </summary>
public class NativePayloadMapping_Tests
{
    private static TextExtractionContext ImageContext()
        => new() { FileExtension = ".png", ContentType = "image/png" };

    private static async Task<TextExtractionResult> ExtractWith(OcrResult ocrResult)
    {
        var sut = TestDoubles.Extractor(TestDoubles.OcrReturning(ocrResult), new[] { TestDoubles.MarkdownProvider(0, ".x") });
        return await sut.ExtractAsync(TestDoubles.Bytes(0xFF, 0xD8), ImageContext());
    }

    [Fact]
    public async Task Should_Map_A_Complete_Native_Payload()
    {
        var result = await ExtractWith(new OcrResult
        {
            Markdown = "md",
            NativePayloadContent = new byte[] { 1, 2, 3, 4 },
            NativePayloadContentType = "application/json",
            NativePayloadSchemaName = "PaddleOCR/PP-StructureV3"
        });

        result.NativePayload.ShouldNotBeNull();
        result.NativePayload!.Content.ShouldBe(new byte[] { 1, 2, 3, 4 });
        result.NativePayload.ContentType.ShouldBe("application/json");
        result.NativePayload.SchemaName.ShouldBe("PaddleOCR/PP-StructureV3");
    }

    [Fact]
    public async Task Should_Map_To_Null_When_There_Is_No_Payload_Content()
    {
        // Providers without a spatial model leave all three flat fields null — a normal path, not an error.
        var result = await ExtractWith(new OcrResult { Markdown = "md" });

        result.NativePayload.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Drop_A_Payload_That_Has_Content_But_No_ContentType()
    {
        // Half-filled: bytes exist but ContentType is missing, so archival cannot label the blob → dropped.
        var result = await ExtractWith(new OcrResult
        {
            Markdown = "md",
            NativePayloadContent = new byte[] { 9, 9 },
            NativePayloadContentType = null,
            NativePayloadSchemaName = "Schema"
        });

        result.NativePayload.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Drop_A_Payload_That_Has_Content_But_No_SchemaName()
    {
        var result = await ExtractWith(new OcrResult
        {
            Markdown = "md",
            NativePayloadContent = new byte[] { 9, 9 },
            NativePayloadContentType = "application/json",
            NativePayloadSchemaName = null
        });

        result.NativePayload.ShouldBeNull();
    }
}
