using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(ExtractTestBaseModule))]
public class DocumentResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The resource is a thin shell delegating to IDocumentAppService.GetAsync. Permission assertions /
        // tenant isolation live in AppService and are represented here by a mock substitute; the injected
        // mock asserts the metadata-header assembly only.
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
    }
}

/// <summary>
/// #350: the document resource metadata header must carry the container / sub-document provenance signal so
/// AI clients can tell a bundle (not consumable) from a sub-document and pivot to its sub-documents.
/// <c>isContainer</c> is always emitted; <c>originDocumentId</c> only when present. These are
/// system-controlled fields, so they are emitted outside the <c>PromptBoundary</c>-wrapped body. The read
/// path is exercised end-to-end (<see cref="DocumentResources.ReadAsync"/>) because <c>BuildPayload</c> is
/// private; AppService behaviors are covered elsewhere and mocked here.
/// </summary>
public class DocumentResources_Tests : ExtractTestBase<DocumentResourcesTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentResources_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    [Fact]
    public async Task Header_emits_isContainer_true_with_no_origin_for_a_container()
    {
        var containerId = Guid.NewGuid();
        _documentAppService
            .GetAsync(containerId)
            .Returns(new DocumentDto
            {
                Id = containerId,
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                CreationTime = new DateTime(2024, 1, 1),
                IsContainer = true,
                OriginDocumentId = null,
                Markdown = "bundle cover"
            });

        var result = await DocumentResources.ReadAsync(containerId.ToString(), _documentAppService);

        var text = ((TextResourceContents)result).Text;
        text.ShouldContain("isContainer: true");
        // Assert the absence of an `originDocumentId:` HEADER LINE specifically, not the substring
        // anywhere in the payload — a prose sentence in the description/body could legitimately contain
        // the word "originDocumentId". Header lines always begin at column 0 (each metadata line is
        // appended with a leading "\n…\n"), so scope the assertion to the metadata header block.
        var header = ExtractMetadataHeader(text);
        header
            .Split('\n')
            .ShouldNotContain(line => line.StartsWith("originDocumentId:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the lines between the metadata header markers (<c>&lt;!-- extract document metadata</c> and the
    /// closing <c>--&gt;</c>) so assertions target header lines only, decoupled from prose wording elsewhere
    /// in the payload (matches <see cref="DocumentResources"/> <c>BuildPayload</c>).
    /// </summary>
    private static string ExtractMetadataHeader(string payload)
    {
        const string open = "<!-- extract document metadata";
        const string close = "-->";
        var start = payload.IndexOf(open, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        var end = payload.IndexOf(close, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return payload.Substring(start, end - start);
    }

    [Fact]
    public async Task Header_emits_isContainer_false_and_origin_for_a_sub_document()
    {
        var subDocId = Guid.NewGuid();
        var originId = Guid.NewGuid();
        _documentAppService
            .GetAsync(subDocId)
            .Returns(new DocumentDto
            {
                Id = subDocId,
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                CreationTime = new DateTime(2024, 1, 1),
                IsContainer = false,
                OriginDocumentId = originId,
                Markdown = "page body"
            });

        var result = await DocumentResources.ReadAsync(subDocId.ToString(), _documentAppService);

        var text = ((TextResourceContents)result).Text;
        text.ShouldContain("isContainer: false");
        text.ShouldContain($"originDocumentId: {originId}");
    }
}
