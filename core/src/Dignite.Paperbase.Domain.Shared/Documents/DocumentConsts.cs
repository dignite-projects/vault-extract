using System;
using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

public static class DocumentConsts
{
    public static int MaxTitleLength { get; set; } = 256;
    /// <summary>操作员拒绝审核时必填的拒绝理由长度上限（#284：从已删除的 ClassificationReason 独立出来）。</summary>
    public static int MaxRejectionReasonLength { get; set; } = 2048;

    /// <summary>
    /// 上传文件大小硬上限（字节，默认 20 MiB，#221）。fail-closed 安全门：
    /// 超限 → loud <c>BusinessException</c>（<c>Document.FileTooLarge</c>），不落 blob、不触发任何 pipeline。
    /// 与前端 document-upload 组件的 20 MB 客户端校验对齐（前端是体验，服务端是边界）。
    /// static mutable——host 可按部署算力 / OCR provider 在启动期上调（与 <see cref="MaxNativePayloadArchiveBytes"/> 同例）。
    /// 上调时需同步上调 host 的 Kestrel <c>MaxRequestBodySize</c> / <c>FormOptions.MultipartBodyLengthLimit</c>。
    /// </summary>
    public static long MaxUploadFileBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// 允许上传的 content-type 白名单（不区分大小写，#221）。fail-closed 安全门：
    /// 不在白名单 → loud <c>BusinessException</c>（<c>Document.UnsupportedFileType</c>），不落 blob、不触发 pipeline。
    /// 默认与前端 document-upload 组件 + "SupportedFormats" 文案对齐（图片 + PDF）。
    /// static mutable——host 若启用 MarkItDown 数字版文档（Word / HTML / CSV 等）按需扩充，并同步 <see cref="AllowedUploadExtensions"/>。
    /// </summary>
    public static ISet<string> AllowedUploadContentTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf"
    };

    /// <summary>
    /// 允许上传的文件扩展名白名单（含前导点，不区分大小写，#221）。与 <see cref="AllowedUploadContentTypes"/> 双重校验：
    /// content-type 客户端可伪造，而扩展名决定 blobName 后缀 + <c>DefaultTextExtractor</c> 的 dispatch
    /// （图片走 OCR / 其他走 Markdown），故二者都要 fail-closed 校验。默认集合与白名单 content-type 对应。
    /// </summary>
    public static ISet<string> AllowedUploadExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf"
    };

    /// <summary>OCR / 抽取阶段检测到的语言 tag（ISO 639-1 或 IETF）。</summary>
    public static int MaxLanguageLength { get; set; } = 16;

    /// <summary>
    /// 原生 payload 归档大小上限（字节，默认 16 MiB，#210）。
    /// 超限 → 记 warning、manifest 置 null、文本提取仍成功（归档 fail-open）。
    /// 可被测试或 host 覆盖（与 MaxTitleLength 等同为 static mutable）。
    /// </summary>
    public static long MaxNativePayloadArchiveBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// 程序化 / LLM 触发检索（MCP 检索 tool 等）单次结果硬上限。
    /// fail-closed 安全门：防 prompt-injection 诱导宽泛查询炸 LLM context / 制造费用攻击。
    /// 设为编译期 <c>const</c>——安全边界不可被运行时配置放大。
    /// </summary>
    public const int MaxSearchResultCount = 50;

    /// <summary>
    /// 程序化 / LLM 触发检索按字段值过滤时的字段值长度上限。
    /// fail-closed 安全门：超长输入直接空结果，不进字段值列扫描，防 DB / CPU 滥用。
    /// </summary>
    public const int MaxSearchFieldValueLength = 512;

    /// <summary>
    /// 单次检索可叠加的 ExtractedFields 字段过滤器数量上限（多字段之间取 AND）。
    /// fail-closed 安全门：防 prompt-injection 灌入海量过滤器撑爆拼接 SQL / 参数数量。
    /// 设为编译期 <c>const</c>——安全边界不可被运行时配置放大。
    /// </summary>
    public const int MaxSearchFieldFilters = 10;
}
