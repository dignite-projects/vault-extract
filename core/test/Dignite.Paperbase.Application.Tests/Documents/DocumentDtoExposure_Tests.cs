using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// #210 review：REST <see cref="DocumentDto"/> 完全不透出 native payload 及 extraction provenance 内部信息——
/// 无下载端点时 payload 摘要不可行动、BlobName 是存储 key 泄漏、step 链是实现细节。纯反射断言，无需 ABP 宿主。
/// </summary>
public class DocumentDtoExposure_Tests
{
    private static readonly string[] PropertyNames =
        typeof(DocumentDto).GetProperties().Select(p => p.Name).ToArray();

    [Fact]
    public void Does_Not_Expose_Native_Payload_Or_Extraction_Provenance()
    {
        // 归档 blob 的内部存储 key 绝不出口。
        PropertyNames.ShouldNotContain("NativePayloadBlobName");

        // ExtractionMetadata / step 链 / ExtractionPath / provider 名都是内部 provenance，不进 DTO。
        PropertyNames.ShouldNotContain("ExtractionMetadata");
        PropertyNames.ShouldNotContain("ExtractionProviderName");
        PropertyNames.ShouldNotContain("ProviderSteps");
        PropertyNames.ShouldNotContain("ExtractionPath");

        // DTO 上不应含任何 "NativePayload" 属性（包括之前的 HasNativePayload / SchemaName / SizeBytes / Sha256 摘要）。
        PropertyNames.ShouldAllBe(n => !n.Contains("NativePayload"));
    }

    [Fact]
    public void Exposes_Extraction_Completeness_Quality_Signal()
    {
        // #268：提取完整性是下游可操作的<b>质量信号</b>（与上面隐藏的内部 provenance 不同）——刻意出口，
        // 让消费方据此决定接收 / 降级 / 进人工复核。正向锁定，防止日后被误删。
        PropertyNames.ShouldContain(nameof(DocumentDto.ExtractionIsComplete));
        PropertyNames.ShouldContain(nameof(DocumentDto.ExtractionIncompleteReason));
    }
}
