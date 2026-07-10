using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class DocumentToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
    }
}

/// <summary>
/// Thin-shell behavior of <see cref="DocumentTools.GetAsync"/>: delegates to
/// <see cref="IDocumentAppService.GetAsync"/> and maps to <see cref="DocumentDetailResult"/>, with title
/// / markdown wrapped by <c>PromptBoundary</c>.
/// </summary>
public class DocumentTools_Tests : VaultExtractTestBase<DocumentToolsTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentTools_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    /// <summary>
    /// #491: Take(N) bounds a result set's row count but not one row's payload, so an uncapped body would let a single
    /// get_document consume the client's whole context window. The body is clipped and the clipping is announced — an
    /// LLM must never mistake a prefix for the whole document.
    /// </summary>
    [Fact]
    public async Task Oversized_markdown_is_clipped_and_the_truncation_is_announced()
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

        var result = await DocumentTools.GetAsync(docId.ToString(), _documentAppService);

        result.MarkdownTruncated.ShouldBeTrue();
        result.MarkdownTotalChars.ShouldBe(body.Length);
        // Truncation happens before WrapDocument, so the boundary tags always survive the cut.
        result.Markdown.ShouldBe(PromptBoundary.WrapDocument(
            new string('x', VaultExtractMcpConsts.MaxDocumentMarkdownChars)));
    }

    /// <summary>#491: the common case stays untouched — a body under the cap reports no truncation.</summary>
    [Fact]
    public async Task Body_under_the_cap_reports_no_truncation()
    {
        var docId = Guid.NewGuid();
        _documentAppService.GetAsync(docId).Returns(new DocumentDto
        {
            Id = docId,
            LifecycleStatus = DocumentLifecycleStatus.Ready,
            CreationTime = new DateTime(2024, 1, 1),
            Markdown = "# Short body"
        });

        var result = await DocumentTools.GetAsync(docId.ToString(), _documentAppService);

        result.MarkdownTruncated.ShouldBeFalse();
        result.MarkdownTotalChars.ShouldBe("# Short body".Length);
    }

    [Fact]
    public async Task Returns_document_with_wrapped_title_and_markdown()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetAsync(docId)
            .Returns(new DocumentDto
            {
                Id = docId,
                Title = "Acme MSA 2025",
                DocumentTypeCode = "contract.general",
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                Language = "zh",
                CreationTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Markdown = "## Acme MSA\n\nContract text here.",
                ExtractionIsComplete = true,
                ExtractedFields = new Dictionary<string, JsonElement>
                {
                    ["amount"] = JsonSerializer.SerializeToElement(100000)
                }
            });

        var result = await DocumentTools.GetAsync(docId.ToString(), _documentAppService);

        result.Id.ShouldBe(docId);
        // Title must be wrapped by PromptBoundary.WrapField.
        result.Title.ShouldBe(PromptBoundary.WrapField("Acme MSA 2025"));
        result.DocumentTypeCode.ShouldBe("contract.general");
        result.LifecycleStatus.ShouldBe("Ready");
        result.Language.ShouldBe("zh");
        // Markdown must be wrapped by PromptBoundary.WrapDocument.
        result.Markdown.ShouldBe(PromptBoundary.WrapDocument("## Acme MSA\n\nContract text here."));
        result.ExtractionIsComplete.ShouldBeTrue();
        result.ExtractedFields.ShouldNotBeNull();
        // Numeric field values pass through unchanged; non-String values are not wrapped.
        result.ExtractedFields!["amount"].GetInt32().ShouldBe(100000);
    }

    [Fact]
    public async Task Wraps_string_extracted_fields()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetAsync(docId)
            .Returns(new DocumentDto
            {
                Id = docId,
                Title = "Test",
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                Markdown = "",
                ExtractionIsComplete = true,
                ExtractedFields = new Dictionary<string, JsonElement>
                {
                    ["party_name"] = JsonSerializer.SerializeToElement("Acme Corp")
                }
            });

        var result = await DocumentTools.GetAsync(docId.ToString(), _documentAppService);

        // Text field values, user-derived free text, must be wrapped by PromptBoundary.WrapField.
        result.ExtractedFields!["party_name"].GetString()
            .ShouldBe(PromptBoundary.WrapField("Acme Corp"));
    }

    [Fact]
    public async Task Throws_on_invalid_id_format()
    {
        await Should.ThrowAsync<McpException>(async () =>
            await DocumentTools.GetAsync("not-a-guid", _documentAppService));
    }

    [Fact]
    public async Task Throws_not_found_when_entity_missing()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetAsync(docId)
            .Throws(new EntityNotFoundException());

        var ex = await Should.ThrowAsync<McpException>(async () =>
            await DocumentTools.GetAsync(docId.ToString(), _documentAppService));

        ex.Message.ShouldContain(docId.ToString());
    }

    [Fact]
    public async Task Exposes_extraction_incomplete_reason()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetAsync(docId)
            .Returns(new DocumentDto
            {
                Id = docId,
                Title = "Incomplete",
                LifecycleStatus = DocumentLifecycleStatus.Ready,
                Markdown = "",
                ExtractionIsComplete = false,
                ExtractionIncompleteReason = "Content truncated by VLM guard"
            });

        var result = await DocumentTools.GetAsync(docId.ToString(), _documentAppService);

        result.ExtractionIsComplete.ShouldBeFalse();
        result.ExtractionIncompleteReason.ShouldBe("Content truncated by VLM guard");
    }
}
