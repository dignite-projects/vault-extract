using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Paperbase.Mcp.Documents;

/// <summary>
/// MCP 检索 tool 返回的单条命中。薄投影——只含"发现文档"所需的系统字段 + 资源 URI；
/// 下游用 <see cref="Uri"/> 走 read_resource 回拉正文（薄载荷 + 回拉，与通道哲学一致）。
/// <see cref="Title"/> 是用户派生自由文本，已在 tool 内经 <c>PromptBoundary.WrapField</c> 包裹。
/// </summary>
public sealed record DocumentSearchResultItem
{
    /// <summary>读取正文的 MCP 资源 URI（<c>paperbase://documents/{id}</c>）。</summary>
    public required string Uri { get; init; }

    public required Guid Id { get; init; }

    /// <summary>展示标题（已 PromptBoundary 包裹）。</summary>
    public string? Title { get; init; }

    public string? DocumentTypeCode { get; init; }

    public required string LifecycleStatus { get; init; }

    public DateTime CreationTime { get; init; }

    /// <summary>
    /// 该文档的类型绑定字段抽取结果（LLM-facing）。key = 字段名（<c>FieldDefinition.Name</c>）；
    /// value 为 <see cref="JsonElement"/>，保留字段声明类型——数字 / 布尔等结构化值原样透传（序列化为 JSON
    /// 数字 / 布尔，下游 LLM 从值本身推断类型，无需字符串转换）；String 类型字段值（用户派生自由文本）经
    /// <c>PromptBoundary.WrapField</c> 包裹后重新装回 JSON 字符串（防 indirect prompt injection）。
    /// 文档无抽取字段（或全为 null）时为 null。
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? ExtractedFields { get; init; }
}
