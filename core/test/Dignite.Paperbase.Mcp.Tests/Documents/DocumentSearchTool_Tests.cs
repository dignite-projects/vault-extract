using System;
using System.Collections.Generic;
using System.Text.Json;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Mcp.Documents;

[DependsOn(typeof(PaperbaseTestBaseModule))]
public class DocumentSearchToolTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 工具是薄壳，委托 IDocumentAppService.GetListAsync；以 mock 注入断言入参组装与结果映射。
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
    }
}

/// <summary>
/// <see cref="DocumentSearchTool"/> 薄壳行为：把 LLM 入参组装成 <see cref="GetDocumentListInput"/> 委托
/// <see cref="IDocumentAppService.GetListAsync"/>，并把 <see cref="DocumentListItemDto"/> 映射成
/// <see cref="DocumentSearchResultItem"/>（title 经 <c>PromptBoundary</c> 包裹）。权限断言、参数校验、
/// 字段定义解析都在 AppService 内（此处以 mock 替身），故那些行为由 AppService 测试覆盖、不在此重复。
/// </summary>
public class DocumentSearchTool_Tests : PaperbaseTestBase<DocumentSearchToolTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentSearchTool_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    [Fact]
    public void Mcp_input_schema_exposes_fieldFilters_and_all_llm_parameters()
    {
        // 必须真正经过 MCP SDK 的 schema 生成（McpServerTool.Create），而不是像其它用例那样直接调
        // DocumentSearchTool.SearchAsync——直接调方法会绕过 ConfigureParameterBinding，永远抓不到本 bug。
        // ABP 的 Autofac 容器把集合关系类型（IReadOnlyList<T> / ICollection<T> / 数组 等）当作可解析的 DI 服务，
        // MCP SDK 会据此把这类参数从 inputSchema 剔除；fieldFilters 必须是具体 List<T> 才会出现在 schema 里。
        // Services 取测试的 Autofac ServiceProvider（基类已 UseAutofac）——MS DI 下复现不出该行为。
        var method = typeof(DocumentSearchTool).GetMethod(nameof(DocumentSearchTool.SearchAsync))!;
        var tool = McpServerTool.Create(
            method, target: null, new McpServerToolCreateOptions { Services = ServiceProvider });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

        // fieldFilters 是回归守护重点：曾因声明为 IReadOnlyList<T> 被 Autofac + MCP SDK 静默剔除出 schema。
        properties.TryGetProperty("fieldFilters", out _).ShouldBeTrue();
        // 其余 LLM-facing 参数对照——它们是标量（IsService=false），本就正常暴露。
        properties.TryGetProperty("documentTypeCode", out _).ShouldBeTrue();
        properties.TryGetProperty("lifecycleStatus", out _).ShouldBeTrue();
        properties.TryGetProperty("maxResultCount", out _).ShouldBeTrue();

        // DI 注入的服务参数必须不出现在 LLM schema 中。
        properties.TryGetProperty("documentAppService", out _).ShouldBeFalse();
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

        // 入参按原意组装传给 AppService（lifecycle 字符串解析 + fieldFilters 透传 + 结果上限）。
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
        // 用户派生自由文本经 PromptBoundary 包裹防 indirect prompt injection。
        result[0].Title.ShouldBe(PromptBoundary.WrapField("Acme MSA"));
    }

    [Fact]
    public async Task Clamps_max_result_count_to_cap()
    {
        _documentAppService
            .GetListAsync(Arg.Any<GetDocumentListInput>())
            .Returns(new PagedResultDto<DocumentListItemDto>(0, new List<DocumentListItemDto>()));

        // LLM 传超大 maxResultCount → clamp 到 MaxSearchResultCount（MCP transport 关注点，保护 LLM context）。
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

        // 无法解析的 lifecycle 字符串 → 当作"不过滤"（不抛错）。
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
                    // 用例层无条件带回的该文档全部抽取字段（原样 JsonElement，保留声明类型）。
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

        // 文本字段值（用户派生自由文本）经 PromptBoundary 包裹后仍是 JSON 字符串（防 indirect prompt injection）；
        // 数字等结构化值原样透传保留 JSON 类型（Number），下游 LLM 从值本身推断类型，无需字符串转换。
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
                    // 多值文本字段（#212）出口是 JSON 数组。
                    ExtractedFields = new Dictionary<string, JsonElement>
                    {
                        ["tags"] = JsonSerializer.SerializeToElement(new[] { "urgent", "legal" })
                    }
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService, documentTypeCode: "contract.general");

        // 数组逐元素经 PromptBoundary 包裹——每个元素都是用户派生自由文本，防 indirect prompt injection。
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
                        // LLM 抽取不符声明类型时兜底存 JSON null（见 ExtractedFieldValueValidator）。
                        ["expiryDate"] = JsonSerializer.SerializeToElement<string?>(null)
                    }
                }
            }));

        var result = await DocumentSearchTool.SearchAsync(
            _documentAppService,
            documentTypeCode: "contract.general");

        // JSON null（抽取兜底）跳过不投影，避免投出误导性的字面 null；只剩有效值字段。
        result[0].ExtractedFields.ShouldNotBeNull();
        result[0].ExtractedFields!.Count.ShouldBe(1);
        var amount = result[0].ExtractedFields!["amount"];
        amount.ValueKind.ShouldBe(JsonValueKind.Number);
        amount.GetInt32().ShouldBe(125000);
        result[0].ExtractedFields!.ContainsKey("expiryDate").ShouldBeFalse();
    }
}
