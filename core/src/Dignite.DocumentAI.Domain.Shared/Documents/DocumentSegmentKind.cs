namespace Dignite.DocumentAI.Documents.Segments;

/// <summary>
/// What kind of source span a <see cref="DocumentSegment"/> was carved from by the unified sub-document detection
/// pass (#371, which folds figure routing #306/#365 and born-digital segmentation #346 into one Markdown-borne
/// pass). Both kinds share one ledger, one identity model (the SHA-256 of the clean span text), and one spawn
/// sink (<c>DerivedDocumentSpawner</c>); the distinction drives only <b>retraction</b> semantics (#364).
/// <para>
/// When a container is reclassified to a concrete type, <see cref="Text"/> children are retracted — they existed
/// only because the parent was a container bundle — while <see cref="Figure"/> children are kept: a genuinely
/// embedded document (an invoice photo inside a contract) survives a concrete-typed parent, exactly as a
/// freshly-uploaded concrete document with an embedded figure would keep it.
/// </para>
/// </summary>
public enum DocumentSegmentKind
{
    /// <summary>A born-digital text span — a constituent of a container bundle (#346).</summary>
    Text = 0,

    /// <summary>
    /// An embedded-figure OCR span (#306): in the marked Markdown it was bracketed by the in-band
    /// <c>[Image OCR]…[End OCR]</c> sentinels. Spawned as a text-only sub-document (the transcription); no image
    /// crop is persisted.
    /// </summary>
    Figure = 1
}
