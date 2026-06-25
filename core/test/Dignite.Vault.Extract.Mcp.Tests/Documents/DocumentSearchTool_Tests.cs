using System;
using System.Collections.Generic;
using System.Text.Json;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(ExtractTestBaseModule))]
public class DocumentSearchToolTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Tool is a thin shell delegating to IDocumentAppService.GetListAsync; injected mock asserts input
        // assembly and result mapping.
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
    }
}

/// <summary>
/// Thin-shell behavior of <see cref="DocumentSearchTool"/>: assemble LLM input into
/// <see cref="GetDocumentListInput"/>, delegate to <see cref="IDocumentAppService.GetListAsync"/>, and map
/// <see cref="DocumentListItemDto"/> to <see cref="DocumentSearchResultItem"/> with title wrapped by
/// <c>PromptBoundary</c>. Permission assertions, parameter validation, and field-definition resolution
/// live in AppService, represented here by a mock substitute, so those behaviors are covered by
/// AppService tests and not repeated here.
/// </summary>
public class DocumentSearchTool_Tests : ExtractTestBase<DocumentSearchToolTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentSearchTool_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    [Fact]
    public void Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters()
    {
        // Must pass through real MCP SDK schema generation (McpServerTool.Create), rather than directly
        // calling DocumentSearchTool.SearchAsync as in other cases. Direct method calls bypass
        // ConfigureParameterBinding and would never catch this bug.
        // ABP's Autofac container treats collection relation types (IReadOnlyList<T> / ICollection<T> /
        // arrays, etc.) as resolvable DI services. The MCP SDK then removes such parameters from
        // inputSchema. fieldFilters must be a concrete List<T> to appear in the schema.
        // Services use the test Autofac ServiceProvider (base class already uses UseAutofac); this cannot
        // be reproduced under MS DI.
        var method = typeof(DocumentSearchTool).GetMethod(nameof(DocumentSearchTool.SearchAsync))!;
        var tool = McpServerTool.Create(
            method, target: null, new McpServerToolCreateOptions { Services = ServiceProvider });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // fieldFilters is the regression guard focus: it was once silently removed from schema because it
        // was declared as IReadOnlyList<T> under Autofac + MCP SDK.
        properties.TryGetProperty("fieldFilters", out _).ShouldBeTrue();
        // Other LLM-facing parameters are controls: they are scalars (IsService=false) and already expose
        // normally.
        properties.TryGetProperty("documentTypeCode", out _).ShouldBeTrue();
        properties.TryGetProperty("originDocumentId", out _).ShouldBeTrue();
        properties.TryGetProperty("lifecycleStatus", out _).ShouldBeTrue();
        properties.TryGetProperty("maxResultCount", out _).ShouldBeTrue();

        // DI-injected service parameters must not appear in the LLM schema.
        properties.TryGetProperty("documentAppService", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Lists_sub_documents_by_origin_without_requiring_a_type()
    {
        // #354: listing a container's sub-documents is a provenance query anchored by originDocumentId, not by
        // type — the children are heterogeneously typed. documentTypeCode must NOT be required in this case, and
        // the parsed origin id must flow into GetDocumentListInput.OriginDocumentId.
        var containerId = Guid.NewGuid();
        var subDocId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(1, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = subDocId,
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    OriginDocumentId = containerId
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService, originDocumentId: containerId.ToString());

        await _documentAppService.Received(1).GetListAsync(Arg.Is<GetDocumentListInput>(i =>
            i.OriginDocumentId == containerId && string.IsNullOrEmpty(i.DocumentTypeCode)));
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(subDocId);
        result[0].OriginDocumentId.ShouldBe(containerId);
    }

    [Fact]
    public async Task Requires_documentTypeCode_unless_originDocumentId_is_given()
    {
        // Neither a type anchor nor an origin anchor: reject loudly instead of silently widening to all types.
        await Should.ThrowAsync<McpException>(() => DocumentSearchTool.SearchAsync(_documentAppService));
        await _documentAppService.DidNotReceive().GetListAsync(Arg.Any<GetDocumentListInput>());
    }

    [Fact]
    public async Task Builds_input_and_maps_wrapped_result()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(1, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = docId,
                    Title = "Acme MSA",
                    DocumentTypeCode = "contract.general",
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1)
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general",
            lifecycleStatus: "Ready",
            fieldFilters: new List<DocumentFieldFilter> { new() { Name = "amount", Min = "100", Max = "200" } });

        // Input is assembled as intended and passed to AppService: lifecycle string parsing,
        // fieldFilters pass-through, and result limit.
        await _documentAppService.Received(1).GetListAsync(Arg.Is<GetDocumentListInput>(i =>
            i.DocumentTypeCode == "contract.general"
            && i.LifecycleStatus == DocumentLifecycleStatus.Ready
            && i.FieldFilters != null && i.FieldFilters.Count == 1
            && i.FieldFilters[0].Name == "amount" && i.FieldFilters[0].Min == "100" && i.FieldFilters[0].Max == "200"
            && i.MaxResultCount == DocumentConsts.MaxSearchResultCount));

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(docId);
        result[0].DocumentTypeCode.ShouldBe("contract.general");
        result[0].LifecycleStatus.ShouldBe("Ready");
        result[0].Uri.ShouldBe(DocumentResourceUri.Format(docId));
        // User-derived free text is wrapped with PromptBoundary to defend against indirect prompt
        // injection.
        result[0].Title.ShouldBe(PromptBoundary.WrapField("Acme MSA"));
    }

    [Fact]
    public async Task Exposes_container_and_origin_provenance_on_result_item()
    {
        // #350: the search result item must surface the container / sub-document provenance signal so AI
        // clients can tell a bundle (not consumable) from a sub-document and pivot to its sub-documents.
        var containerId = Guid.NewGuid();
        var subDocId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(2, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = containerId,
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    IsContainer = true,
                    OriginDocumentId = null
                },
                new()
                {
                    Id = subDocId,
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    IsContainer = false,
                    OriginDocumentId = containerId
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService, documentTypeCode: "contract.general");

        // System-controlled provenance fields pass through verbatim (no PromptBoundary, no inlined sub-doc list).
        result[0].IsContainer.ShouldBeTrue();
        result[0].OriginDocumentId.ShouldBeNull();
        result[1].IsContainer.ShouldBeFalse();
        result[1].OriginDocumentId.ShouldBe(containerId);
    }

    [Fact]
    public async Task Clamps_max_result_count_to_cap()
    {
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(0, new List<DocumentListItemDto>()));

        // Oversized maxResultCount from the LLM is clamped to MaxSearchResultCount, an MCP transport
        // concern that protects LLM context.
        await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general",
            maxResultCount: 1000);

        await _documentAppService.Received(1).GetListAsync(Arg.Is<GetDocumentListInput>(i =>
            i.MaxResultCount == DocumentConsts.MaxSearchResultCount));
    }

    [Fact]
    public async Task Unparseable_lifecycle_is_ignored()
    {
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(0, new List<DocumentListItemDto>()));

        // Unparseable lifecycle string is treated as no filter, without throwing.
        await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general",
            lifecycleStatus: "NotAStatus");

        await _documentAppService.Received(1).GetListAsync(Arg.Is<GetDocumentListInput>(i =>
            i.LifecycleStatus == null));
    }

    [Fact]
    public async Task Maps_extracted_field_values_wrapping_only_strings()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(1, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = docId,
                    Title = "Acme MSA",
                    DocumentTypeCode = "contract.general",
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    // Test fixture returns all extracted fields for this document unconditionally, as raw
                    // JsonElement values that preserve declared types.
                    ExtractedFields = new Dictionary<string, JsonElement>
                    {
                        ["partyName"] = JsonSerializer.SerializeToElement("Acme Corp"),
                        ["amount"] = JsonSerializer.SerializeToElement(125000)
                    }
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general");

        // Text field values, user-derived free text, remain JSON strings after PromptBoundary wrapping to
        // defend against indirect prompt injection. Structured values such as numbers pass through
        // unchanged and preserve their JSON type (Number), so downstream LLMs can infer type from the
        // value without string conversion.
        result[0].ExtractedFields.ShouldNotBeNull();
        var partyName = result[0].ExtractedFields!["partyName"];
        partyName.ValueKind.ShouldBe(JsonValueKind.String);
        partyName.GetString().ShouldBe(PromptBoundary.WrapField("Acme Corp"));
        var amount = result[0].ExtractedFields!["amount"];
        amount.ValueKind.ShouldBe(JsonValueKind.Number);
        amount.GetInt32().ShouldBe(125000);
    }

    [Fact]
    public async Task Wraps_each_element_of_multi_value_string_array()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(1, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = docId,
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    // Multi-value text field (#212) output is a JSON array.
                    ExtractedFields = new Dictionary<string, JsonElement>
                    {
                        ["tags"] = JsonSerializer.SerializeToElement(new[] { "urgent", "legal" })
                    }
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService, documentTypeCode: "contract.general");

        // Array elements are individually wrapped by PromptBoundary because each element is
        // user-derived free text and needs indirect prompt-injection defense.
        var tags = result[0].ExtractedFields!["tags"];
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe(PromptBoundary.WrapField("urgent"));
        tags[1].GetString().ShouldBe(PromptBoundary.WrapField("legal"));
    }

    [Fact]
    public async Task Skips_null_valued_extracted_fields()
    {
        var docId = Guid.NewGuid();
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(1, new List<DocumentListItemDto>
            {
                new()
                {
                    Id = docId,
                    LifecycleStatus = DocumentLifecycleStatus.Ready,
                    CreationTime = new DateTime(2024, 1, 1),
                    ExtractedFields = new Dictionary<string, JsonElement>
                    {
                        ["amount"] = JsonSerializer.SerializeToElement(125000),
                        // When LLM extraction does not match the declared type, fallback storage is JSON
                        // null. See ExtractedFieldValueValidator.
                        ["expiryDate"] = JsonSerializer.SerializeToElement<string?>(null)
                    }
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general");

        // JSON null from extraction fallback is skipped from projection, avoiding misleading literal null
        // output; only valid value fields remain.
        result[0].ExtractedFields.ShouldNotBeNull();
        result[0].ExtractedFields!.Count.ShouldBe(1);
        var amount = result[0].ExtractedFields!["amount"];
        amount.ValueKind.ShouldBe(JsonValueKind.Number);
        amount.GetInt32().ShouldBe(125000);
        result[0].ExtractedFields!.ContainsKey("expiryDate").ShouldBeFalse();
    }
}
