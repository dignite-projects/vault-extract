using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents;

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
    /// Per-extension upload content-type allow-list, case-insensitive (#221 / #471). Fail-closed safety gate:
    /// an extension/content-type pair not present here -> loud <c>BusinessException</c>
    /// (<c>Document.UnsupportedFileType</c>), no blob write, no pipeline trigger.
    /// Generic MIME types such as application/octet-stream / application/zip remain excluded.
    /// </summary>
    public static IDictionary<string, ISet<string>> AllowedUploadContentTypesByExtension { get; set; } =
        new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = ContentTypes("image/jpeg"),
            [".jpeg"] = ContentTypes("image/jpeg"),
            [".png"] = ContentTypes("image/png"),
            [".gif"] = ContentTypes("image/gif"),
            [".webp"] = ContentTypes("image/webp"),
            [".pdf"] = ContentTypes("application/pdf"),
            [".csv"] = ContentTypes("text/csv", "application/csv", "application/vnd.ms-excel"),
            [".tsv"] = ContentTypes("text/tab-separated-values", "text/tsv"),
            [".txt"] = ContentTypes("text/plain"),
            [".docx"] = ContentTypes("application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            // .pptx extraction requires the OpenXml module (VaultExtractParseOpenXmlModule): ElBruno has no
            // PresentationML converter, so with that module absent a .pptx degrades to an empty document rather
            // than graceful text (unlike .docx, which the ElBruno catch-all can still convert).
            [".pptx"] = ContentTypes("application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".xlsx"] = ContentTypes("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
        };

    /// <summary>
    /// Returns whether the exact extension/content-type pair is supported. Extension and MIME comparisons are
    /// case-insensitive, but MIME parameters and generic fallbacks are intentionally not normalized into acceptance.
    /// </summary>
    public static bool IsAllowedUploadType(string extension, string contentType)
    {
        return !string.IsNullOrEmpty(extension) &&
               !string.IsNullOrEmpty(contentType) &&
               AllowedUploadContentTypesByExtension.TryGetValue(extension, out var contentTypes) &&
               contentTypes.Contains(contentType);
    }

    private static ISet<string> ContentTypes(params string[] values)
        => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

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
    /// Length of <see cref="Document.FieldFingerprint"/> (#411): the SHA-256 (lowercase hex) of this document type's
    /// normalized unique-key field values, used to detect duplicate re-uploads of the same business entity. Matches
    /// <see cref="FileOriginConsts.MaxContentHashLength"/> (both are SHA-256 hex).
    /// </summary>
    public static int MaxFieldFingerprintLength { get; set; } = 64;

    /// <summary>
    /// Hard cap on the number of duplicate-candidate document Ids surfaced to the operator (#411). Fail-closed bound
    /// on the fingerprint-collision query so a fingerprint shared by many documents cannot return an unbounded set.
    /// </summary>
    public const int MaxDuplicateCandidates = 20;

    /// <summary>
    /// Native payload archive size limit in bytes, default 16 MiB (#210).
    /// Over limit -> log warning, set manifest null, and text extraction still succeeds (archive fails open).
    /// May be overridden by tests or host, same static mutable pattern as MaxTitleLength.
    /// </summary>
    public static long MaxNativePayloadArchiveBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Blob key prefix of retained embedded-figure images (#477): a figure blob lives at
    /// <c>extraction-figures/{documentId}/{contentHash}</c>, so the key itself names its <b>owning</b> document.
    /// The #478 reclaim rules key on this structure: a <c>FileOrigin</c> pointing under another document's prefix
    /// is a <b>borrowed</b> shared blob (a figure sub-document sharing its source's image — never copied), deleted
    /// only by whichever referencing side is permanently deleted last. A <c>const</c> (not host-mutable): it is a
    /// persisted storage-key format, changing it would orphan existing blobs.
    /// </summary>
    public const string FigureBlobNamePrefix = "extraction-figures/";

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
