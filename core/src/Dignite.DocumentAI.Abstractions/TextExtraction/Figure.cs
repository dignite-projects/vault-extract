namespace Dignite.DocumentAI.Abstractions.TextExtraction;

/// <summary>
/// An embedded figure surfaced by a text-extraction provider as an <b>out-of-band</b> signal (#306):
/// the decoded image bytes plus its OCR transcription and minimal provenance. Orthogonal to the
/// inline-into-Markdown figure output (#301) — inlining stays the document's text payload; this object
/// is the separate channel that lets the pipeline persist a candidate crop and route a figure that is
/// itself a document to its own derived <see cref="Documents.Document"/> (Scenario B).
/// <para>
/// <b>Identity is content, not position.</b> A figure is keyed downstream by the SHA-256 of
/// <see cref="Content"/> (which doubles as the derived document's <c>FileOrigin.ContentHash</c> /
/// <c>OriginFigureKey</c>), never by bbox — bbox drifts across provider / re-extraction (#210). The
/// transient <see cref="PageNumber"/> is provenance only.
/// </para>
/// </summary>
public sealed class Figure
{
    /// <summary>Decoded image-file bytes (PNG / JPEG), the same bytes fed to <c>IOcrProvider.RecognizeAsync</c>.</summary>
    public byte[] Content { get; }

    /// <summary>Image MIME type, for example <c>image/png</c>.</summary>
    public string ContentType { get; }

    /// <summary>
    /// OCR transcription of the figure (Markdown). This is the input the sub-document routing gate
    /// classifies against the source's tenant type layer; it is also already inlined into the source
    /// document's Markdown at the figure's reading position (#301).
    /// </summary>
    public string Transcription { get; }

    /// <summary>1-based source page the figure was found on, or <c>null</c> when the format has no page concept. Provenance only.</summary>
    public int? PageNumber { get; }

    public Figure(byte[] content, string contentType, string transcription, int? pageNumber = null)
    {
        Content = content;
        ContentType = contentType;
        Transcription = transcription;
        PageNumber = pageNumber;
    }
}
