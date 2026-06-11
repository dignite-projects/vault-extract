using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dignite.DocumentAI.Mcp.Documents;

/// <summary>
/// MCP 检索 tool：薄壳——把 LLM 入参组装成 <see cref="GetDocumentListInput"/> 后委托
/// <see cref="IDocumentAppService.GetListAsync"/>（与 REST 列表同一个用例入口：权限断言、参数校验、
/// 字段定义解析、租户隔离都在 AppService 内统一执行）。检索收敛为结构化字段查询——元数据 +
/// 一个或多个 ExtractedFields 字段值过滤器（多个之间 AND），无 keyword 全文 / 子串检索
/// （退化检索引擎属 OUT of scope，见 CLAUDE.md / Issue #204）。字段值查询锚定 <c>documentTypeCode</c>（required）：
/// amount 等字段离开类型无确定含义，引导下游 AI 客户端在用户未指定类型时**反问澄清**
/// （澄清发生在客户端 LLM，DocumentAI 不实现对话 agent）。
/// 命中行直接带回该文档的全部 ExtractedFields（省二次回拉）；本 tool 只承担传输层关注点：容错解析
/// lifecycle 字符串、结果集硬上限 clamp 到 <see cref="DocumentConsts.MaxSearchResultCount"/>（保护 LLM context）、
/// title 与字段值经 <c>PromptBoundary</c> 包裹防 indirect prompt injection。
/// </summary>
[McpServerToolType]
public sealed class DocumentSearchTool
{
    [McpServerTool(Name = "search_docai_documents", Title = "Search Documents", ReadOnly = true)]
    [Description("Search DocumentAI documents within a single document type by structured metadata "
        + "and/or one or more extracted-field filters (all combined with AND). Returns up to 50 rows "
        + "(id, uri, title, type, lifecycle, created-at, and the document's extracted field values); read a "
        + "match's full Markdown via its docai://documents/{id} resource uri. Structured field/metadata "
        + "search only — no keyword/full-text or semantic/vector retrieval. documentTypeCode is required; if the "
        + "user has not said which document type to search, ask them first. Discover a type's filterable field "
        + "names and data types via its docai://document-types/{code} resource.")]
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
        // 必须是具体 List<T>——不可改成 IReadOnlyList<T> / IEnumerable<T> / ICollection<T> / 数组：ABP 用 Autofac
        // 容器，它把所有集合关系类型当作可隐式解析的 DI 服务（IServiceProviderIsService.IsService 返回 true），
        // MCP SDK 的参数绑定据此把该参数从 LLM 看到的 inputSchema 中剔除（ExcludeFromSchema）——LLM 永远看不到
        // 这个参数、字段过滤静默失效。守护见 DocumentSearchTool_Tests 的 schema 测试 +
        // .claude/rules/llm-call-anti-patterns.md 反例 C。
        List<DocumentFieldFilter>? fieldFilters = null,
        [Description("Max rows to return (1-50). Defaults to 50.")]
        int? maxResultCount = null,
        CancellationToken cancellationToken = default)
    {
        // documentTypeCode 是 required 契约（每次检索锚定单一文档类型——字段值查询需要它解析每个字段
        // 的数据类型；description 亦明示 required）。空串会让 GetListAsync 退化成跨所有类型的元数据检索，
        // 与契约相悖——fail-loud 拒绝而非静默放宽（权限 / 租户 / 上限仍在 AppService 内生效，非安全风险）。
        if (string.IsNullOrWhiteSpace(documentTypeCode))
        {
            throw new McpException(
                "documentTypeCode is required: every search anchors to a single document type. "
                + "If the user has not said which type to search, ask them first; discover a type's "
                + "filterable fields via its docai://document-types/{code} resource.");
        }

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
                ExtractedFields = DocumentFieldProjection.Project(d.ExtractedFields)
            })
            .ToList();
    }
}
