using System.Collections.Generic;
using Volo.Abp.Domain.Values;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文档文本提取的 <b>provenance 元数据</b>（持久化值对象，#210）。整体序列化进 <c>Document.ExtractionMetadata</c> 的
/// JSON 列（<c>AbpJsonValueConverter</c> + 手写 <c>ValueComparer</c>，套路同 <c>ExportTemplate.Columns</c>）。
/// <para>
/// 类名前缀 <c>Text</c> 消歧：field extraction（字段抽取）vs text extraction（文本提取）。
/// </para>
/// <para>
/// 承载：胜出 provider 名 + 原生 payload 归档清单（<see cref="NativePayloadManifest"/>，
/// 可空——未归档时 null）。原始 bbox / cell 等空间信号<b>留 blob</b>，本类只存 manifest。
/// </para>
/// <para>
/// get-only 属性 + 唯一带参构造让 System.Text.Json 反序列化复用同一构造（参数名匹配属性名），套路同 <c>ExportColumn</c>。
/// </para>
/// </summary>
public class DocumentTextExtractionMetadata : ValueObject
{
    /// <summary>
    /// 胜出 provider 的家族 / 名称（如 <c>PaddleOCR</c> / <c>AzureDocumentIntelligence</c> / <c>ElBruno.MarkItDotNet</c>）；未知时 null。
    /// <para>
    /// <b>不是解析入口。</b>将来解析归档的原生 payload 时，认 <see cref="NativePayloadManifest.SchemaName"/>（带到 model 粒度，
    /// 如 <c>PaddleOCR/PP-StructureV3</c>）——同一 provider 不同 model 的结构不同（PP-StructureV3 页级、bbox 占位
    /// vs PP-OCRv4 行级），仅凭家族名会选错解析器。本字段只是 payload 缺席（数字版 / 归档失败，manifest 为 null）时
    /// 唯一的"谁产出了 Markdown"兜底溯源。
    /// </para>
    /// </summary>
    public string? ProviderName { get; }

    /// <summary>原生 payload 归档清单；未归档（无 payload / 超限 / 写失败）时为 <c>null</c>。</summary>
    public NativePayloadManifest? NativePayloadManifest { get; }

    /// <summary>
    /// 本次文本提取是否<b>完整</b>（#268）。<c>true</c> = 已捕获全部内容；<c>false</c> = 已知有缺失
    /// （OCR 输出被截断 / 命中重复守卫被丢弃 / 多页 PDF 有页未能转写）。历史记录（构造时未给）默认 <c>true</c>，
    /// 视为完整——它们先于本信号产生。
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>不完整时的简短诊断说明；完整时为 <c>null</c>。</summary>
    public string? IncompleteReason { get; }

    public DocumentTextExtractionMetadata(
        string? providerName,
        NativePayloadManifest? nativePayloadManifest,
        bool isComplete = true,
        string? incompleteReason = null)
    {
        ProviderName = providerName;
        NativePayloadManifest = nativePayloadManifest;
        IsComplete = isComplete;
        IncompleteReason = incompleteReason;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        // ABP 的 ValueEquals 用 SequenceEqual + 默认比较器，对 atomic 走 object.Equals——嵌套 ValueObject
        // 不会递归 ValueEquals（默认比较器退化为引用相等）。故这里把 NativePayloadManifest 的各原子值<b>展平</b>
        // 进父序列，让父级 ValueEquals 真正结构化深比较；可空成员统一取空串/0 占位避免 null atomic + 区分
        // "manifest 缺席" 与 "manifest 各字段恰为默认值"（前缀 NULL sentinel）。
        yield return ProviderName ?? string.Empty;

        // 完整性原子值在 manifest 的 yield break 之前产出——否则 manifest 为 null 时会被跳过，漏入相等性比较。
        yield return IsComplete;
        yield return IncompleteReason ?? string.Empty;

        if (NativePayloadManifest is null)
        {
            yield return "\0null-manifest";
            yield break;
        }

        yield return NativePayloadManifest.BlobName;
        yield return NativePayloadManifest.ContentType;
        yield return NativePayloadManifest.SizeBytes;
        yield return NativePayloadManifest.Sha256;
        yield return NativePayloadManifest.SchemaName;
    }
}
