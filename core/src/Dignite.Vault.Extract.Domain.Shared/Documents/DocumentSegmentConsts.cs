namespace Dignite.Vault.Extract.Documents.Segments;

/// <summary>
/// Length limits for <see cref="DocumentSegment"/> columns (#346, born-digital path). Mirrors
/// <c>DocumentFigureConsts</c>: the segment key is bounded; the slice text is intentionally unbounded
/// (nvarchar(max), like <c>Document.Markdown</c> and <c>DocumentFigure.Transcription</c>) because it is a
/// Markdown slice used to seed the derived document, never indexed.
/// </summary>
public static class DocumentSegmentConsts
{
    /// <summary>
    /// Length of <see cref="DocumentSegment.SegmentKey"/>: the SHA-256 (lowercase hex) of the slice text,
    /// which equals the spawned derived document's <c>OriginConstituentKey</c>.
    /// </summary>
    public static int MaxSegmentKeyLength { get; set; } = 64;
}
