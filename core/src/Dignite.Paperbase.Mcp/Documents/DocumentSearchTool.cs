using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using ModelContextProtocol.Server;

namespace Dignite.Paperbase.Mcp.Documents;

/// <summary>
/// MCP 检索 tool：薄壳——把 LLM 入参组装成 <see cref="GetDocumentListInput"/> 后委托
/// <see cref="IDocumentAppService.GetListAsync"/>（与 REST 列表同一个用例入口：权限断言、参数校验、
/// 字段定义解析、租户隔离都在 AppService 内统一执行）。检索收敛为结构化字段查询——元数据 +
/// 一个或多个 ExtractedFields 字段值过滤器（多个之间 AND），无 keyword 全文 / 子串检索
/// （退化检索引擎属 OUT of scope，见 CLAUDE.md / Issue #204）。字段值查询锚定 <c>documentTypeCode</c>（required）：
/// amount 等字段离开类型无确定含义，引导下游 AI 客户端在用户未指定类型时**反问澄清**
/// （澄清发生在客户端 LLM，Paperbase 不实现对话 agent）。
/// 命中行直接带回该文档的全部 ExtractedFields（省二次回拉）；本 tool 只承担传输层关注点：容错解析
/// lifecycle 字符串、结果集硬上限 clamp 到 <see cref="DocumentConsts.MaxSearchResultCount"/>（保护 LLM context）、
/// title 与字段值经 <c>PromptBoundary</c> 包裹防 indirect prompt injection。
/// </summary>
[McpServerToolType]
public sealed class DocumentSearchTool
{
    [McpServerTool(Name = "search_paperbase_documents")]
    [Description("Search Paperbase documents within a single document type by structured metadata "
        + "and/or one or more extracted-field filters (all combined with AND). Returns up to 50 rows "
        + "(id, uri, title, type, lifecycle, created-at, and the document's extracted field values); read a "
        + "match's full Markdown via its paperbase://documents/{id} resource uri. Structured field/metadata "
        + "search only — no keyword/full-text or semantic/vector retrieval. documentTypeCode is required; if the "
        + "user has not said which document type to search, ask them first. Discover a type's filterable field "
        + "names and data types via its paperbase://document-types/{code} resource.")]
    public static async Task<IReadOnlyList<DocumentSearchResultItem>> SearchAsync(
        IDocumentAppService documentAppService,
        [Description("Required. The document type code to search within (e.g. a classification result like "
            + "'contract.general'). Every search anchors to a single document type; a field value query needs it "
            + "to resolve each field's data type. If unknown, ask the user which document type to search.")]
        string documentTypeCode,
        [Description("Filter by lifecycle status. One of: Uploaded, Processing, Ready, Failed, Archived. Optional.")]
        string? lifecycleStatus = null,
        [Description("Extracted-field filters, all combined with AND (every filter must match). Each entry "
            + "names a field defined on the document type plus either an exact Value or an inclusive numeric/date "
            + "Min/Max range. Each field's data type is resolved server-side. Omit for a metadata-only search. Optional.")]
        IReadOnlyList<DocumentFieldFilter>? fieldFilters = null,
        [Description("Max rows to return (1-50). Defaults to 50.")]
        int? maxResultCount = null,
        CancellationToken cancellationToken = default)
    {
        // 容错解析 lifecycle 过滤值——LLM 客户端通常传字符串名（"Ready"）。无法解析则当作"不过滤"
        // （filter 缺失只会多返回结果，受结果上限约束；权限 / 租户 / 上限在 AppService 内仍生效）。
        DocumentLifecycleStatus? lifecycle = null;
        if (!string.IsNullOrWhiteSpace(lifecycleStatus)
            && Enum.TryParse<DocumentLifecycleStatus>(lifecycleStatus, ignoreCase: true, out var parsedLifecycle))
        {
            lifecycle = parsedLifecycle;
        }

        var input = new GetDocumentListInput
        {
            DocumentTypeCode = documentTypeCode,
            LifecycleStatus = lifecycle,
            FieldFilters = fieldFilters?.ToList(),
            // 结果集硬上限 clamp 到 MaxSearchResultCount——MCP transport 关注点（保护 LLM context /
            // 防 prompt-injection 诱导宽泛查询）。REST 列表走正常分页，不受此约束。
            MaxResultCount = Math.Clamp(
                maxResultCount ?? DocumentConsts.MaxSearchResultCount, 1, DocumentConsts.MaxSearchResultCount),
            SkipCount = 0
        };

        // 委托 AppService 用例：权限断言（CheckPolicyAsync）、DTO 参数校验、字段定义解析、租户隔离、
        // 字段值过滤都在内部统一执行。MCP dispatch 不经 HTTP [Authorize]，但 AppService 方法体内的
        // CheckPolicyAsync 是真正的强制门，照常生效——这是 LLM 路径的权限防线。
        var result = await documentAppService.GetListAsync(input);

        return result.Items
            .Select(d => new DocumentSearchResultItem
            {
                Uri = DocumentResourceUri.Format(d.Id),
                Id = d.Id,
                // 用户派生自由文本（title）经 PromptBoundary 包裹，防 indirect prompt injection。
                Title = PromptBoundary.WrapField(d.Title),
                DocumentTypeCode = d.DocumentTypeCode,
                LifecycleStatus = d.LifecycleStatus.ToString(),
                CreationTime = d.CreationTime,
                // 该文档的全部抽取字段值转 LLM-facing（String 包裹 / 结构化裸值 / null 跳过）。
                ExtractedFields = ProjectFields(d.ExtractedFields)
            })
            .ToList();
    }

    /// <summary>
    /// 把文档的 ExtractedFields（原样 <see cref="JsonElement"/>）转成 LLM-facing 投影，保留声明类型：
    /// 数字 / 布尔等结构化值原样透传——下游 LLM 从值本身推断类型，无需字符串转换；
    /// String 类型值（用户派生自由文本）经 <c>PromptBoundary.WrapField</c> 包裹后重新装回 JSON 字符串
    /// 防 indirect prompt injection——注入风险 ⟺ 值是 JSON 字符串，故仅 <see cref="JsonValueKind.String"/> 需包裹
    /// （JSON 无原生 date 类型，日期以字符串存储会一并被包裹，冗余但无害）。
    /// JSON null（LLM 抽取不符声明类型时的兜底值，见 <c>ExtractedFieldValueValidator</c>）跳过不投影——
    /// 无有效值，避免投出误导性的字面 null。全部跳过 / 无字段 → 返回 null。
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement>? ProjectFields(
        IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var projected = new Dictionary<string, JsonElement>(fields.Count);
        foreach (var pair in fields)
        {
            switch (pair.Value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    continue;
                case JsonValueKind.String:
                    // 用户派生自由文本 → 包裹后重新装回 JSON 字符串（仍是 String，序列化为带分隔符的引号值）。
                    projected[pair.Key] = JsonSerializer.SerializeToElement(
                        PromptBoundary.WrapField(pair.Value.GetString()));
                    break;
                default:
                    // 数字 / 布尔等结构化值原样透传——保留 JSON 类型，下游 LLM 从值本身推断类型。
                    projected[pair.Key] = pair.Value;
                    break;
            }
        }

        return projected.Count > 0 ? projected : null;
    }
}
