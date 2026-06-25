namespace Dignite.Vault.Extract.Ocr;

/// <summary>
/// OCR provider output. Markdown-first: implementations <b>must</b> populate
/// <see cref="Markdown"/>. Even if the underlying service only returns plain text, the provider must
/// wrap it as flat Markdown paragraphs internally so downstream consumers receive one format.
/// </summary>
/// <remarks>
/// Out-of-band signals (per-page bbox / stamp locations / form key-value pairs / page-level metadata)
/// are <b>orthogonal to the text payload</b>. Their <b>raw</b> carrier is the three flat fields such as
/// <see cref="NativePayloadContent"/> (#210: archived to blob, not DB, and not stuffed back into
/// <see cref="Markdown"/>). If normalized capabilities such as page-aware citations are needed later,
/// they should be added as named optional strongly typed fields on this class, for example
/// <c>IReadOnlyList&lt;PageBlock&gt;? PageBlocks</c>. They must not be stuffed back into the
/// <see cref="Markdown"/> string or carried through a <c>Dictionary&lt;string, object&gt;</c> extension
/// slot. Open a separate issue for each normalized out-of-band signal.
/// </remarks>
public class OcrResult
{
    /// <summary>
    /// Structured Markdown output. Empty string when the provider recognizes no content.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>Detected primary language in BCP 47 format.</summary>
    public string? DetectedLanguage { get; set; }

    /// <summary>OCR provider family/name for auditability.</summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Whether this recognition result is <b>complete</b> (#268). <c>true</c> (default) means the
    /// provider believes all content was captured; <c>false</c> means content is known to be missing,
    /// such as output truncated by token limits, duplicate-guard drops, or pages in a multi-page PDF
    /// that could not be transcribed. Providers that do not support a completeness concept
    /// (PaddleOCR / Azure DI) keep the default <c>true</c>, preserving behavior.
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>Short diagnostic when incomplete (<see cref="IsComplete"/> is false); <c>null</c> when complete.</summary>
    public string? IncompleteReason { get; set; }

    // === Flat native payload (#210) ===
    // The former three OcrNativePayload fields were moved up directly, eliminating the dedicated
    // wrapper class inside the Ocr project. DefaultTextExtractor maps them to
    // TextExtractionResult.NativePayload (Abstractions.NativePayload). Providers without a spatial
    // model set all three fields to null, which is treated as no payload.

    /// <summary>Opaque provider-native output bytes, usually UTF-8 encoded raw provider JSON response; null when there is no payload.</summary>
    public byte[]? NativePayloadContent { get; set; }

    /// <summary>Payload MIME type, for example <c>application/json</c>; null when there is no payload.</summary>
    public string? NativePayloadContentType { get; set; }

    /// <summary>Schema identifier for downstream consumers to choose a parser, for example <c>PaddleOCR/PP-StructureV3</c>; null when there is no payload.</summary>
    public string? NativePayloadSchemaName { get; set; }
}
