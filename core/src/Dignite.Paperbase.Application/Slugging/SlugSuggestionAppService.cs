using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// 显示名 → 机器标识（slug）建议（issue #190）。FieldDefinition / DocumentType 创建表单共用。
///
/// <para>
/// 这是 Paperbase 中**首个同步 request/response 形态的 LLM 调用点**（其余 LLM 调用都在
/// BackgroundJob / EventHandler 中）。安全约定（CLAUDE.md "## 安全约定" /
/// .claude/rules/llm-call-anti-patterns.md）逐条对齐：
/// </para>
/// <list type="number">
///   <item>**Fail-closed 权限**：类级 <c>[Authorize(ConfirmClassification)]</c>——这是真实的 HTTP
///         AppService（经 SlugSuggestionController 暴露），属性在 HTTP 边界生效，与
///         FieldDefinitionAppService / DocumentTypeAppService 一致。</item>
///   <item>**无 DB 查询**：纯文本 → 文本，不落任何 <c>IRepository</c> / raw SQL，因而 Take(N) /
///         显式 TenantId 谓词不适用。</item>
///   <item>**PromptBoundary**：用户派生自由文本 Label 进 prompt 前经
///         <see cref="PromptBoundary.WrapField"/> 包裹 + 追加 <see cref="PromptBoundary.BoundaryRule"/>。</item>
///   <item>**编译期常量 instructions**：<see cref="SlugSystemPrompt"/> 是 <c>const</c>，不拼接任何运行时字符串。</item>
///   <item>**不信任 LLM 输出**：结果经 <see cref="SlugNormalizer.Sanitize"/> 限定为 <c>[a-z0-9_]</c>，且仅作为 admin 可改的
///         **建议**——最终 Create 仍走 FieldDefinition/DocumentType 的白名单校验。</item>
/// </list>
/// </summary>
[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class SlugSuggestionAppService : PaperbaseAppService, ISlugSuggestionAppService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<SlugSuggestionAppService> _logger;

    public SlugSuggestionAppService(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        ILogger<SlugSuggestionAppService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// 服务端硬超时。前端 8s 超时只保护浏览器侧——非 Angular 调用方、保持连接不放的客户端、
    /// 或不及时响应 request-abort 的 provider 仍可能拖住请求处理与 token 配额。作为首个交互式
    /// request/response LLM 路径，后端必须有自己的 deadline 兜底（取略大于前端 8s 的值）。
    /// </summary>
    private static readonly TimeSpan SuggestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 编译期常量 system instructions。**不允许**拼接任何运行时字符串（防 prompt injection）。
    /// </summary>
    private const string SlugSystemPrompt =
        "You convert a human-readable label into a short machine identifier (a \"slug\"). " +
        "Translate non-English labels into concise English first, then form the slug. " +
        "Output rules: lowercase ASCII snake_case using only letters a-z, digits 0-9 and single " +
        "underscores between words; 1 to 3 words; at most 64 characters; no leading or trailing " +
        "underscore; no spaces; no punctuation other than underscores; no quotes. " +
        "Examples: \"合同金额\" -> \"contract_amount\"; \"甲方名称\" -> \"party_name\"; \"発行日\" -> \"issue_date\". " +
        "Return JSON only in the form {\"slug\": \"...\"}.";

    public virtual async Task<SlugSuggestionDto> SuggestAsync(
        SuggestSlugInput input,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SlugSystemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
            // Label 是用户派生自由文本 —— 经 PromptBoundary.WrapField 显式标记为数据。
            new(ChatRole.User, "Label:\n" + PromptBoundary.WrapField(input.Label))
        };

        // 超时 + fail-open 外壳收敛到 InteractiveLlmCall（与 FieldDraftSuggestionAppService 共用，#264 review #10）：
        // 客户端取消原样上抛，服务端超时 / provider 故障 → null，ExtractSlug(null) 回退空 slug → 前端本地占位。
        var rawJson = await InteractiveLlmCall.TryGetResponseTextAsync(
            _chatClient, messages, SlugResponseFormat, SuggestTimeout, _logger, "Slug suggestion", cancellationToken);

        return new SlugSuggestionDto { Slug = ExtractSlug(rawJson) };
    }

    /// <summary>
    /// 从 LLM 的 JSON 输出里取出 <c>slug</c> 字段并 sanitize。任何解析失败 → 空字符串（前端回退）。
    /// </summary>
    protected virtual string ExtractSlug(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("slug", out var slugProp) &&
                slugProp.ValueKind == JsonValueKind.String)
            {
                return SlugNormalizer.Sanitize(slugProp.GetString());
            }

            // JSON 合法但 schema 漂移（缺 slug 键 / 非字符串）——回退仍生效，但记一条便于离线分析模型行为。
            _logger.LogWarning("Slug suggestion JSON missing a string 'slug' property: {Raw}", rawJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Slug suggestion returned non-JSON output: {Raw}", rawJson);
        }

        return string.Empty;
    }

    private static readonly ChatResponseFormat SlugResponseFormat = CreateSlugResponseFormat();

    private static ChatResponseFormat CreateSlugResponseFormat()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["slug"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = @"^[a-z0-9_]{1,64}$",
                    ["description"] = "A lowercase ASCII snake_case slug."
                }
            },
            ["required"] = new JsonArray("slug"),
            ["additionalProperties"] = false
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "PaperbaseSlugSuggestion",
            schemaDescription: "A single suggested Paperbase machine identifier.");
    }
}
