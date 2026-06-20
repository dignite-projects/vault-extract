using System;
using System.Collections.Generic;

namespace Dignite.Extract.Documents;

public static class DocumentConsts
{
    public static int MaxTitleLength { get; set; } = 256;
    /// <summary>Maximum length for the rejection reason required when an operator rejects review (#284: split out from the removed ClassificationReason).</summary>
    public static int MaxRejectionReasonLength { get; set; } = 2048;

    /// <summary>
    /// Hard upload file size limit in bytes, default 20 MiB (#221). Fail-closed safety gate:
    /// over limit -> loud <c>BusinessException</c> (<c>Document.FileTooLarge</c>), no blob write, no pipeline trigger.
    /// Aligned with the frontend document-upload component's 20 MB client-side validation; frontend is UX, server is the boundary.
    /// Static mutable: host may raise this at startup according to deployment compute / OCR provider, same as <see cref="MaxNativePayloadArchiveBytes"/>.
    /// When raising it, also raise host Kestrel <c>MaxRequestBodySize</c> / <c>FormOptions.MultipartBodyLengthLimit</c>.
    /// </summary>
    public static long MaxUploadFileBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Allow-list for upload content-type, case-insensitive (#221). Fail-closed safety gate:
    /// not in allow-list -> loud <c>BusinessException</c> (<c>Document.UnsupportedFileType</c>), no blob write, no pipeline trigger.
    /// Defaults align with the frontend document-upload component + "SupportedFormats" copy (images + PDF).
    /// Static mutable: if the host enables MarkItDown digital-native documents such as Word / HTML / CSV, extend this as needed
    /// and keep <see cref="AllowedUploadExtensions"/> in sync.
    /// </summary>
    public static ISet<string> AllowedUploadContentTypes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf"
    };

    /// <summary>
    /// Allow-list for upload file extensions, including leading dot and case-insensitive (#221). Double-validated with
    /// <see cref="AllowedUploadContentTypes"/>: content-type is client-spoofable, while extension determines blobName suffix
    /// and <c>DefaultTextExtractor</c> dispatch (images go to OCR, others to Markdown), so both must be fail-closed validated.
    /// Defaults correspond to the content-type allow-list.
    /// </summary>
    public static ISet<string> AllowedUploadExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf"
    };

    /// <summary>Language tag detected during OCR / extraction, ISO 639-1 or IETF.</summary>
    public static int MaxLanguageLength { get; set; } = 16;

    /// <summary>
    /// Length of <see cref="Document.OriginConstituentKey"/> (#306 / #346): the SHA-256 (lowercase hex) of the
    /// source constituent — the figure's bytes (image path) or the Markdown slice (born-digital path) — which
    /// equals the derived document's <c>FileOrigin.ContentHash</c>. Matches
    /// <see cref="FileOriginConsts.MaxContentHashLength"/>.
    /// </summary>
    public static int MaxOriginConstituentKeyLength { get; set; } = 64;

    /// <summary>
    /// Native payload archive size limit in bytes, default 16 MiB (#210).
    /// Over limit -> log warning, set manifest null, and text extraction still succeeds (archive fails open).
    /// May be overridden by tests or host, same static mutable pattern as MaxTitleLength.
    /// </summary>
    public static long MaxNativePayloadArchiveBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Hard per-call result limit for programmatic / LLM-triggered retrieval, such as MCP search tools.
    /// Fail-closed safety gate: prevents prompt injection from inducing broad queries that explode LLM context or create cost attacks.
    /// Compile-time <c>const</c>: the safety boundary cannot be enlarged by runtime configuration.
    /// </summary>
    public const int MaxSearchResultCount = 50;

    /// <summary>
    /// Field value length limit for programmatic / LLM-triggered retrieval by field value filters.
    /// Fail-closed safety gate: overlong input returns an empty result directly and does not scan field value columns, preventing DB / CPU abuse.
    /// </summary>
    public const int MaxSearchFieldValueLength = 512;

    /// <summary>
    /// Maximum number of ExtractedFields filters that can be combined in one retrieval call; multiple fields are ANDed.
    /// Fail-closed safety gate: prevents prompt injection from flooding huge filter counts and exploding generated SQL / parameter counts.
    /// Compile-time <c>const</c>: the safety boundary cannot be enlarged by runtime configuration.
    /// </summary>
    public const int MaxSearchFieldFilters = 10;

    /// <summary>
    /// Number of document Ids read / enqueued per bulk reprocessing dispatch batch (#289). Dispatchers chain themselves:
    /// each batch uses keyset pagination to read only Ids (<c>WHERE Id &gt; lastId ORDER BY Id Take(N)</c>),
    /// enqueues N single-document jobs, then enqueues the next dispatcher. Each dispatcher is a short task lasting seconds
    /// and does not occupy a worker for long. Static mutable: host may tune it according to LLM provider concurrency / DB connection pool.
    /// </summary>
    public static int ReprocessingDispatchBatchSize { get; set; } = 100;
}
