namespace Dignite.Vault.Extract.Documents.Segments;

/// <summary>
/// Routing lifecycle of a <see cref="DocumentSegment"/> (#346, born-digital path). The segmentation job uses
/// this as a durable, resumable work-queue marker: it processes <see cref="Pending"/> segments, so a crash
/// mid-routing resumes only the unfinished ones without duplicate-spawning or re-paying the LLM split.
/// Mirrors <c>DocumentFigureStatus</c> — the two constituent paths (image figures / born-digital slices) share
/// the same status model and feed the same derived-document sink.
/// </summary>
public enum DocumentSegmentStatus
{
    /// <summary>Not yet spawned into a derived <c>Document</c> (the initial state after the LLM split).</summary>
    Pending = 0,

    /// <summary>Spawned into a derived <c>Document</c> (recorded in <see cref="DocumentSegment.RoutedDocumentId"/>).</summary>
    Spawned = 1
}
