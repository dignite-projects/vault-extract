namespace Dignite.DocumentAI.Documents.Figures;

/// <summary>
/// Length limits for <see cref="DocumentFigure"/> columns (#306). Mirrors <see cref="FileOriginConsts"/>
/// for the storage-key / content-type / hash fields; the transcription is intentionally unbounded
/// (nvarchar(max), like <c>Document.Markdown</c>) because it is a figure-OCR snapshot, never indexed.
/// </summary>
public static class DocumentFigureConsts
{
    public static int MaxContentHashLength { get; set; } = 64;

    public static int MaxCropBlobNameLength { get; set; } = 512;

    public static int MaxContentTypeLength { get; set; } = 256;
}
