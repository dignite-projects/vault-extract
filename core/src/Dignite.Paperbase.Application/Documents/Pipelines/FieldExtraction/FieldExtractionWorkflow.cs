using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Pipelines.FieldExtraction;

/// <summary>
/// 统一字段抽取工作流（字段架构 v2）。按 <see cref="FieldExtractionDescriptor"/> 列表用 LLM
/// 单次调用提取字段值——不区分 Host 字段 / 租户字段（来源由调用方决定）。
/// <para>
/// 设计要点：
/// <list type="bullet">
///   <item>所有字段一次调用提取，减少 LLM 往返 + 上下文重复</item>
///   <item>用 <c>ChatResponseFormat.Json</c> 限定输出为 JSON</item>
///   <item>归一化由 prompt 要求 AI 按 <see cref="FieldDataType"/> 输出规范形（数字裸 JSON number、
///         日期 ISO-8601 字符串、布尔 JSON true/false）；解析时再经 <see cref="ExtractedFieldValueValidator"/>
///         严格校验（不符声明类型的值写 null + log）——保证 <c>ExtractedFields</c> 类型自洽
///         （Issue #204：让 GetFieldMatchedIdsAsync 的类型化查询建立在干净数据上）</item>
///   <item>所有字段的 prompt（包括 Host 来源）统一经 <c>PromptBoundary.WrapField</c> 包裹——
///         比 v1 区分 Host/Tenant 是否 wrap 更保守，无功能损失</item>
/// </list>
/// </para>
/// </summary>
public class FieldExtractionWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly PaperbaseAIBehaviorOptions _behaviorOptions;
    private readonly ILogger<FieldExtractionWorkflow> _logger;

    public FieldExtractionWorkflow(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<PaperbaseAIBehaviorOptions> behaviorOptions,
        ILogger<FieldExtractionWorkflow> logger)
    {
        _chatClient = chatClient;
        _behaviorOptions = behaviorOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// 按字段定义批量抽取。返回 (字段名 → JsonElement) 字典；缺失/无法解析的字段以 null 形式出现。
    /// </summary>
    public virtual async Task<IReadOnlyDictionary<string, JsonElement?>> ExtractAsync(
        IReadOnlyList<FieldExtractionDescriptor> fields,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0)
        {
            return new Dictionary<string, JsonElement?>();
        }

        var truncated = markdown.Length > _behaviorOptions.MaxTextLengthPerExtraction
            ? markdown[.._behaviorOptions.MaxTextLengthPerExtraction]
            : markdown;

        // system role 保持**编译期常量** —— 防 prompt injection（CLAUDE.md "## 安全约定 / Description / Instructions 编译期常量"）。
        // 字段 schema（含租户用户输入的 f.Name / f.Prompt）放进 user role 第一条 message，
        // 让模型把"指令"与"用户数据"分开看待——配合 PromptBoundary.WrapField + BoundaryRule。
        // FieldDefinition.Name 在实体层已强制白名单 [A-Za-z0-9_-]{1,64}（参见 FieldDefinitionConsts.NamePattern），
        // 不会含换行 / 引号 / Markdown 控制字符；f.Prompt 长度受 MaxPromptLength 上限保护。
        var schemaMessage = BuildSchemaUserMessage(fields);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemInstructions + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, schemaMessage),
            new(ChatRole.User, PromptBoundary.WrapDocument(truncated))
        };

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var rawJson = response.Text?.Trim() ?? string.Empty;

        return ParseJsonToDictionary(rawJson, fields);
    }

    /// <summary>
    /// 编译期常量 system instructions。**不允许**拼接任何运行时字符串。
    /// </summary>
    private const string SystemInstructions =
        "You extract structured fields from a Markdown document. " +
        "The first user message lists the fields to extract (schema), each annotated with its data type. " +
        "The second user message contains the document body. " +
        "Return JSON only with one key per requested field. " +
        "Normalize each value to its declared data type: " +
        "Number as a bare JSON number, integer or decimal (strip currency symbols, thousands separators, and units; " +
        "use '.' as the decimal point and '-' for negatives); " +
        "Date as an ISO-8601 \"YYYY-MM-DD\" JSON string; " +
        "DateTime as an offset-free ISO-8601 \"YYYY-MM-DDThh:mm:ss\" JSON string (local wall-clock time, no timezone offset or trailing Z); " +
        "Boolean as JSON true or false; " +
        "String as the original text. " +
        "When a field cannot be confidently extracted or normalized to its declared type, set its value to null. " +
        "The input document is provided as Markdown — treat headings, tables, and lists as semantic structure signals.";

    private static string BuildSchemaUserMessage(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fields to extract:");
        foreach (var f in fields)
        {
            // f.Name 已经在 FieldDefinition 实体层经白名单 regex 校验，仅含 [A-Za-z0-9_-]。
            // f.Prompt 来自 Host 编译期常量或租户用户输入 —— 经 PromptBoundary.WrapField 显式标记为数据，
            // BoundaryRule 让模型把它当指令以外的内容看待。
            sb.AppendLine($"- \"{f.Name}\" ({f.DataType}, {(f.IsRequired ? "required" : "optional")}): {PromptBoundary.WrapField(f.Prompt)}");
        }
        return sb.ToString();
    }

    private IReadOnlyDictionary<string, JsonElement?> ParseJsonToDictionary(
        string rawJson,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var result = new Dictionary<string, JsonElement?>(fields.Count);

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            foreach (var f in fields) result[f.Name] = null;
            return result;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Field extraction returned non-JSON output: {Raw}", rawJson);
            foreach (var f in fields) result[f.Name] = null;
            return result;
        }

        foreach (var field in fields)
        {
            if (!root.TryGetProperty(field.Name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                result[field.Name] = null;
                continue;
            }

            // 强校验：值必须符合声明的 FieldDataType（与操作员手改路径
            // DocumentAppService.UpdateExtractedFieldsAsync 共用 ExtractedFieldValueValidator）。
            // 归一化责任在 prompt（AI 输出规范形）；此处是兜底护栏，不做强制转换——
            // 不符声明类型的值写 null + log，保证字段值类型自洽
            // （让 DocumentExtractedField 的类型化列查询建立在干净数据上）。
            if (!ExtractedFieldValueValidator.IsValid(prop, field.DataType))
            {
                _logger.LogWarning(
                    "Field extraction value for '{FieldName}' did not match declared type {DataType} (JSON kind {JsonValueKind}); storing null.",
                    field.Name, field.DataType, prop.ValueKind);
                result[field.Name] = null;
                continue;
            }

            // 校验通过：保留原始 JsonElement（已是规范 JSON 类型），避免双重转换 + 精度损失。
            result[field.Name] = prop;
        }

        return result;
    }
}
