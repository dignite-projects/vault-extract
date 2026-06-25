namespace Dignite.Vault.Extract.Abstractions.Parse;

/// <summary>
/// <b>Native output payload</b> from the text extraction provider: out-of-band spatial-signal material
/// orthogonal to the <see cref="TextExtractionResult.Markdown"/> text payload, such as bbox / table
/// cells / text-span anchors / confidence / region types.
/// <para>
/// <b>General provider capability</b>, not OCR-only: OCR maps this up through <c>OcrResult</c>; rich
/// Markdown providers, such as future Docling / liteparse-like providers, may fill it directly; pure
/// text-to-Markdown providers such as ElBruno / MarkItDown have no spatial model and leave it
/// <c>null</c>. The text extraction job archives it to blob: it <b>does not enter the DB</b>, is
/// <b>not stuffed back into the Markdown string</b>, and is <b>not exposed as a parallel text field on
/// the contract</b>.
/// </para>
/// </summary>
public sealed class NativePayload
{
    /// <summary>Opaque native output bytes, usually UTF-8 encoded provider raw-response JSON.</summary>
    public byte[] Content { get; }

    /// <summary>Payload MIME type, for example <c>application/json</c>.</summary>
    public string ContentType { get; }

    /// <summary>Schema identifier for downstream consumers to choose a parser, such as <c>PaddleOCR/PP-StructureV3</c> / <c>AzureDocumentIntelligence.AnalyzeResult</c>.</summary>
    public string SchemaName { get; }

    public NativePayload(byte[] content, string contentType, string schemaName)
    {
        Content = content;
        ContentType = contentType;
        SchemaName = schemaName;
    }
}
