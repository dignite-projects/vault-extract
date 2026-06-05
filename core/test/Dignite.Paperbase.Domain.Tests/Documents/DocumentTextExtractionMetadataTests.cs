using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// #218：<see cref="DocumentTextExtractionMetadata"/> / <see cref="NativePayloadManifest"/> 继承
/// ABP <c>ValueObject</c> 后获得结构化相等性（经 <c>ValueEquals</c> 暴露——ABP 10.x 的 ValueObject 不覆写
/// <c>Equals</c>/<c>GetHashCode</c>/<c>==</c>，结构化比较是 opt-in 的 <c>ValueEquals</c>），且不破坏
/// System.Text.Json 序列化往返（套路同 ExportColumn）。纯单元测试，无需 ABP host。
/// </summary>
public class DocumentTextExtractionMetadataTests
{
    private static NativePayloadManifest CreateManifest() =>
        new("extraction-native/doc-1", "application/json", 1234, "abc123", "PaddleOCR/PP-StructureV3");

    [Fact]
    public void Manifest_With_Same_Values_Should_Be_ValueEqual()
    {
        CreateManifest().ValueEquals(CreateManifest()).ShouldBeTrue();
    }

    [Fact]
    public void Manifest_With_Different_Values_Should_Not_Be_ValueEqual()
    {
        var other = new NativePayloadManifest(
            "extraction-native/doc-1", "application/json", 1234, "DIFFERENT", "PaddleOCR/PP-StructureV3");
        CreateManifest().ValueEquals(other).ShouldBeFalse();
    }

    [Fact]
    public void Metadata_With_Same_Values_Including_Nested_Manifest_Should_Be_ValueEqual()
    {
        var a = new DocumentTextExtractionMetadata("PaddleOCR", CreateManifest());
        var b = new DocumentTextExtractionMetadata("PaddleOCR", CreateManifest());

        a.ValueEquals(b).ShouldBeTrue();
    }

    [Fact]
    public void Metadata_With_Null_Members_Should_Be_ValueEqual_And_Differ_From_Populated()
    {
        var emptyA = new DocumentTextExtractionMetadata(null, null);
        var emptyB = new DocumentTextExtractionMetadata(null, null);

        emptyA.ValueEquals(emptyB).ShouldBeTrue();
        emptyA.ValueEquals(new DocumentTextExtractionMetadata("PaddleOCR", null)).ShouldBeFalse();
        emptyA.ValueEquals(new DocumentTextExtractionMetadata(null, CreateManifest())).ShouldBeFalse();
    }

    [Fact]
    public void Json_Roundtrip_Should_Preserve_Value_Equality()
    {
        var original = new DocumentTextExtractionMetadata("PaddleOCR", CreateManifest());

        var json = JsonSerializer.Serialize(original);
        var roundtripped = JsonSerializer.Deserialize<DocumentTextExtractionMetadata>(json);

        roundtripped.ShouldNotBeNull();
        roundtripped!.ValueEquals(original).ShouldBeTrue();
    }

    [Fact]
    public void Completeness_Participates_In_Value_Equality_And_Json_Roundtrip()
    {
        var complete = new DocumentTextExtractionMetadata("VisionLlm", null);
        var incomplete = new DocumentTextExtractionMetadata(
            "VisionLlm", null, isComplete: false, incompleteReason: "2 of 5 page(s) were not fully transcribed.");

        // 完整性纳入结构化相等性（即便 manifest 为 null）。
        complete.ValueEquals(incomplete).ShouldBeFalse();

        var roundtripped = JsonSerializer.Deserialize<DocumentTextExtractionMetadata>(
            JsonSerializer.Serialize(incomplete));
        roundtripped.ShouldNotBeNull();
        roundtripped!.IsComplete.ShouldBeFalse();
        roundtripped.IncompleteReason.ShouldBe("2 of 5 page(s) were not fully transcribed.");
        roundtripped.ValueEquals(incomplete).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_Json_Without_Completeness_Deserializes_As_Complete()
    {
        // 向后兼容（#268）：#268 之前持久化的行没有完整性字段 → 经构造器可选参数默认值视为完整。
        var legacyJson = "{\"ProviderName\":\"PaddleOCR\",\"NativePayloadManifest\":null}";

        var metadata = JsonSerializer.Deserialize<DocumentTextExtractionMetadata>(legacyJson);

        metadata.ShouldNotBeNull();
        metadata!.IsComplete.ShouldBeTrue();
        metadata.IncompleteReason.ShouldBeNull();
    }
}
