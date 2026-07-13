using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
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
public class DocumentResources_Tests : VaultExtractTestBase<DocumentResourcesTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentResources_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    [Fact]
    public async Task Reads_explicit_tenant_resource_in_its_uri_scope()
    {
        var tenantId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var currentTenant = GetRequiredService<ICurrentTenant>();
        _documentAppService.GetAsync(documentId).Returns(_ =>
        {
            currentTenant.Id.ShouldBe(tenantId);
            return Task.FromResult(new DocumentDto
            {
                Id = documentId,
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                CreationTime = new DateTime(2024, 1, 1),
                Markdown = "# Tenant document"
            });
        });

        var result = await DocumentResources.ReadTenantScopedAsync(
            tenantId.ToString(),
            documentId.ToString(),
            _documentAppService,
            serviceProvider: ServiceProvider);

        ((TextResourceContents)result).Uri.ShouldBe(DocumentResourceUri.Format(documentId, tenantId));
        // No post-await ambient check — it would be a tautology: the resource read is an async method, so
        // the ICurrentTenant.Change it makes internally never flows back to this caller's ExecutionContext,
        // and the assertion would pass even if the using-scope were removed. The stubbed callback above
        // asserts the scope was actually applied during the call.
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
    /// #491: the resource body is capped for the same reason the search tool caps its row count — one read must not
    /// exhaust the client's context window. The cut is announced in the metadata header (system-controlled values, so
    /// outside the PromptBoundary), and the <c>&lt;document&gt;</c> wrapper survives it.
    /// </summary>
    [Fact]
    public async Task Oversized_body_is_clipped_and_the_header_announces_it()
    {
        var docId = Guid.NewGuid();
        var body = new string('x', VaultExtractMcpConsts.MaxDocumentMarkdownChars + 500);
        _documentAppService.GetAsync(docId).Returns(new DocumentDto
        {
            Id = docId,
            LifecycleStatus = DocumentLifecycleStatus.Ready,
            CreationTime = new DateTime(2024, 1, 1),
            Markdown = body
        });

        var result = await DocumentResources.ReadAsync(docId.ToString(), _documentAppService);

        var text = ((TextResourceContents)result).Text!;
        var header = ExtractMetadataHeader(text);
        header.ShouldContain("markdownTruncated: true");
        header.ShouldContain($"markdownTotalChars: {body.Length}");
        text.ShouldEndWith("</document>");
        text.ShouldNotContain(new string('x', VaultExtractMcpConsts.MaxDocumentMarkdownChars + 1));
    }

    /// <summary>#491: a body under the cap must not gain header noise — the truncation lines appear only on a real cut.</summary>
    [Fact]
    public async Task Body_under_the_cap_adds_no_truncation_header()
    {
        var docId = Guid.NewGuid();
        _documentAppService.GetAsync(docId).Returns(new DocumentDto
        {
            Id = docId,
            LifecycleStatus = DocumentLifecycleStatus.Ready,
            CreationTime = new DateTime(2024, 1, 1),
            Markdown = "# Short body"
        });

        var result = await DocumentResources.ReadAsync(docId.ToString(), _documentAppService);

        var header = ExtractMetadataHeader(((TextResourceContents)result).Text!);
        header.ShouldNotContain("markdownTruncated");
        header.ShouldNotContain("markdownTotalChars");
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

    [Fact]
    public async Task Header_emits_cabinet_id_when_document_is_filed()
    {
        var documentId = Guid.NewGuid();
        var cabinetId = Guid.NewGuid();
        _documentAppService
            .GetAsync(documentId)
            .Returns(new DocumentDto
            {
                Id = documentId,
                CabinetId = cabinetId,
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                CreationTime = new DateTime(2024, 1, 1),
                Markdown = "filed document"
            });

        var result = await DocumentResources.ReadAsync(documentId.ToString(), _documentAppService);

        var header = ExtractMetadataHeader(((TextResourceContents)result).Text);
        header.ShouldContain($"cabinetId: {cabinetId}");
    }
}
