using System.Collections.Generic;

namespace Dignite.Paperbase.Mcp.Documents;

/// <summary>
/// 文档类型字段 schema（MCP resource <c>paperbase://document-types/{code}</c> 的 read 投影，LLM-facing）。
/// 让下游 AI 客户端发现某类型有哪些字段、什么数据类型，据此给检索 tool 的 <c>fieldFilters</c> / <c>includeFields</c>
/// 填对字段名。<see cref="DisplayName"/> 是 admin 配置的用户派生文本，已经 <c>PromptBoundary.WrapField</c> 包裹
/// （防 indirect prompt injection）；<see cref="TypeCode"/> / 字段 <c>Name</c> / <c>DataType</c> 是系统受控值
/// （白名单 / 枚举），裸值。
/// </summary>
public sealed record DocumentTypeSchema
{
    public required string TypeCode { get; init; }

    /// <summary>类型显示名（已 PromptBoundary 包裹）。</summary>
    public string? DisplayName { get; init; }

    public required IReadOnlyList<DocumentTypeFieldSchema> Fields { get; init; }
}

/// <summary>
/// 单个字段的 schema 投影。<see cref="Name"/> 是 immutable 标识符（用于 <c>fieldFilters</c> / <c>includeFields</c>）；
/// <see cref="DataType"/> 决定可用查询算子（String / Boolean 仅等值，数字 / 日期可区间）；
/// <see cref="DisplayName"/> 已 PromptBoundary 包裹。不含抽取指令 <c>Prompt</c>——抽取指令对查询 / 投影编排无用，
/// 省 LLM context + 注入面。
/// </summary>
public sealed record DocumentTypeFieldSchema
{
    public required string Name { get; init; }

    /// <summary>字段数据类型（<c>FieldDataType</c> 枚举名：String / Number / Boolean / Date / DateTime）。</summary>
    public required string DataType { get; init; }

    /// <summary>字段显示名（已 PromptBoundary 包裹）。</summary>
    public string? DisplayName { get; init; }

    public bool IsRequired { get; init; }
}
