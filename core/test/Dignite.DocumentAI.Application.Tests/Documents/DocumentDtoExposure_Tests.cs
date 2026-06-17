using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.Documents;

/// <summary>
/// #210 review: REST <see cref="DocumentDto"/> must not expose native payload or internal extraction
/// provenance information. Without a download endpoint, payload summaries are not actionable; BlobName is
/// a storage-key leak; and step chains are implementation details. Pure reflection assertion; no ABP host required.
/// </summary>
public class DocumentDtoExposure_Tests
{
    private static readonly string[] PropertyNames =
        typeof(DocumentDto).GetProperties().Select(p => p.Name).ToArray();

    [Fact]
    public void Does_Not_Expose_Native_Payload_Or_Extraction_Provenance()
    {
        // Internal storage keys for archived blobs must never be exported.
        PropertyNames.ShouldNotContain("NativePayloadBlobName");

        // ExtractionMetadata / step chain / ExtractionPath / provider name are internal provenance and do not enter DTOs.
        PropertyNames.ShouldNotContain("ExtractionMetadata");
        PropertyNames.ShouldNotContain("ExtractionProviderName");
        PropertyNames.ShouldNotContain("ProviderSteps");
        PropertyNames.ShouldNotContain("ExtractionPath");

        // DTO should not contain any "NativePayload" property, including prior HasNativePayload / SchemaName /
        // SizeBytes / Sha256 summaries.
        PropertyNames.ShouldAllBe(n => !n.Contains("NativePayload"));
    }

    [Fact]
    public void Exposes_Container_Marker()
    {
        // #346: the container marker is an intentional egress field — downstream must see it to skip building a
        // record from a container. Positive lock to prevent accidental future removal.
        PropertyNames.ShouldContain(nameof(DocumentDto.IsContainer));
    }

    [Fact]
    public void Exposes_Extraction_Completeness_Quality_Signal()
    {
        // #268: extraction completeness is an actionable downstream quality signal, unlike the hidden internal
        // provenance above. It is intentionally exported so consumers can decide whether to accept, degrade,
        // or enter manual review. Positive lock to prevent accidental future removal.
        PropertyNames.ShouldContain(nameof(DocumentDto.ExtractionIsComplete));
        PropertyNames.ShouldContain(nameof(DocumentDto.ExtractionIncompleteReason));
    }
}
