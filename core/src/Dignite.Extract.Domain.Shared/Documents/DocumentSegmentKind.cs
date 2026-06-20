using System;

namespace Dignite.Extract.Documents.Segments;

/// <summary>
/// What kind of source span a <see cref="DocumentSegment"/> was carved from by the unified sub-document detection
/// pass (#371, which folds figure routing #306/#365 and born-digital segmentation #346 into one Markdown-borne
/// pass). Both kinds share one ledger, one identity model (the SHA-256 of the clean span text), and one spawn
/// sink (<c>DerivedDocumentSpawner</c>); the distinction <b>forks behaviour at several sites</b> — the central
/// property being that a <see cref="Figure"/> is <b>orthogonal to its source's container-ness</b> while a
/// <see cref="Text"/> span is container-bound. That property is centralised in
/// <see cref="DocumentSegmentKindExtensions.IsContainerIndependent"/> (an exhaustive switch — a third kind forces it
/// to declare its stance) and drives: the spawn gate and the still-spawnable guard (a Figure spawns even on a
/// concrete-typed parent; a Text span spawns only while the source is a container), and the container→concrete
/// retraction (#364: <see cref="Text"/> children are retracted — they existed only because the parent was a bundle —
/// while <see cref="Figure"/> children are kept, exactly as a freshly-uploaded concrete document keeps an embedded
/// figure). The per-span clean-text derivation (<c>ExtractBodies</c> for a figure body vs <c>Strip</c> for a text
/// span) is decided at carve time from the slice's opening marker, before the kind is recorded.
/// </summary>
public enum DocumentSegmentKind
{
    /// <summary>A born-digital text span — a constituent of a container bundle (#346).</summary>
    Text = 0,

    /// <summary>
    /// An embedded-figure OCR span (#306): in the document Markdown it was bracketed by the in-band
    /// <c>*[Image OCR]*…*[End OCR]*</c> provenance markers. Spawned as a text-only sub-document (the transcription);
    /// no image crop is persisted.
    /// </summary>
    Figure = 1
}

/// <summary>
/// Compiler-/runtime-checked semantics for <see cref="DocumentSegmentKind"/> (#379 LOW): the scattered
/// <c>Kind == Figure</c> / <c>Kind == Text</c> comparisons that fork spawn + retraction behaviour are funnelled
/// through one exhaustive switch, so adding a third kind forces this single declaration to be revisited (the
/// <c>default</c> arm throws as a runtime backstop) instead of silently defaulting at each decision site — guarding
/// against a future #364-class missed-branch bug.
/// </summary>
public static class DocumentSegmentKindExtensions
{
    /// <summary>
    /// Whether a span of this kind is <b>orthogonal to its source's container-ness</b> (#364/#371): a
    /// <see cref="DocumentSegmentKind.Figure"/> is a genuinely embedded document, so it spawns even on a
    /// concrete-typed parent and <b>survives</b> a container→concrete reclassification; a
    /// <see cref="DocumentSegmentKind.Text"/> exists only as a container-bundle constituent, so it spawns only while
    /// the source is a container and is <b>retracted</b> when the source is reclassified to a concrete type.
    /// </summary>
    public static bool IsContainerIndependent(this DocumentSegmentKind kind) => kind switch
    {
        DocumentSegmentKind.Figure => true,
        DocumentSegmentKind.Text => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Unhandled DocumentSegmentKind; declare its container-orthogonality stance here.")
    };
}
