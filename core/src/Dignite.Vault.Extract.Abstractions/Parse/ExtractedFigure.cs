namespace Dignite.Vault.Extract.Abstractions.Parse;

/// <summary>
/// An embedded figure's <b>source image</b>, surfaced out-of-band (#477) so the Application layer can persist it
/// as a blob and the Markdown can reference it (<c>figures/{ContentHash}</c>). A provider only surfaces bytes it
/// <b>already decoded for OCR</b> — it persists nothing itself (respecting the Parse/OCR ↔ Application boundary,
/// #393). <see cref="TextExtractionResult.Figures"/> is <c>null</c> unless figure retention is requested
/// (<see cref="TextExtractionContext.RetainFigureImages"/>); providers with no spatial model leave it <c>null</c>.
/// <para>
/// This is a named, strongly-typed, nullable out-of-band signal (the #210 <c>PageBlocks</c>/<c>NativePayload</c>
/// precedent — <b>not</b> a <c>Dictionary&lt;string,object&gt;</c> bag, #206). The image bytes never enter
/// <c>Document.Markdown</c>; only the <c>figures/{hash}</c> reference does.
/// </para>
/// </summary>
public sealed class ExtractedFigure
{
    /// <summary>
    /// SHA-256 (lowercase hex) of <see cref="Content"/> — the stable identity + dedup key. It matches the
    /// <c>figures/{hash}</c> reference the provider inlined into the Markdown, and becomes the blob key suffix
    /// (<c>extraction-figures/{documentId}/{hash}</c>) the Application layer writes.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>The image bytes — the exact OCR input the provider decoded.</summary>
    public byte[] Content { get; }

    /// <summary>Image MIME type, e.g. <c>image/png</c> / <c>image/jpeg</c>.</summary>
    public string ContentType { get; }

    /// <summary>1-based source page anchor (provenance, never identity, #210); <c>null</c> for a page-less source.</summary>
    public int? PageNumber { get; }

    /// <summary>Native alt-text / caption when the format provides it (e.g. DOCX <c>docPr.Description</c>); <c>null</c> otherwise.</summary>
    public string? AltText { get; }

    public ExtractedFigure(string contentHash, byte[] content, string contentType, int? pageNumber = null, string? altText = null)
    {
        ContentHash = contentHash;
        Content = content;
        ContentType = contentType;
        PageNumber = pageNumber;
        AltText = altText;
    }
}
