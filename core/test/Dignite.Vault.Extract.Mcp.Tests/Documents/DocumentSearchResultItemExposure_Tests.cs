using System.Linq;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #210: MCP output from the search tool's <see cref="DocumentSearchResultItem"/> must <b>never expose</b>
/// native payload / provenance metadata. AI clients do not need it, and internal archive BlobName is a
/// storage key. Pure reflection assertion; no ABP host required.
/// </summary>
public class DocumentSearchResultItemExposure_Tests
{
    [Fact]
    public void Mcp_Search_Result_Excludes_Extraction_Provenance_And_Native_Payload()
    {
        var propertyNames = typeof(DocumentSearchResultItem).GetProperties().Select(p => p.Name).ToArray();

        propertyNames.ShouldNotContain("ExtractionProviderName");
        propertyNames.ShouldNotContain("ExtractionMetadata");
        propertyNames.ShouldNotContain("ExtractionPath");
        propertyNames.ShouldNotContain("ProviderSteps");
        propertyNames.ShouldNotContain("HasNativePayload");

        // No field containing "NativePayload" / "BlobName" should appear in the MCP projection.
        propertyNames.ShouldAllBe(n =>
            !n.Contains("NativePayload") && !n.Contains("BlobName"));
    }
}
