using System;

namespace Dignite.Vault.Extract.Documents.Segments;

/// <summary>
/// What kind of source span a <see cref="DocumentSegment"/> was carved from by the unified sub-document detection
/// pass (#371). Behaviourally this is a single bit consulted at one lifecycle moment — when a container source is
/// reclassified to a concrete type (#364): a <see cref="Text"/> child is retracted (it existed only as a bundle
/// constituent) while a <see cref="Figure"/> child is kept (an embedded document is orthogonal to its source's
/// container-ness). That stance is centralised in
/// <see cref="DocumentSegmentKindExtensions.IsContainerIndependent"/> (an exhaustive switch — a third kind forces it
/// to declare its stance).
/// <para>
/// Both values are written by fresh detection (#494). #487 Phase A had briefly retired <see cref="Figure"/> —
/// deleting the figure-image retention chain (#477/#478) and, with it, the routing of embedded standalone
/// documents — but only the retention chain was the thing being abandoned: once <c>Document.FileOrigin</c> went
/// back to nullable, a figure child became exactly what a text child already is, a Markdown slice with no blob.
/// A figure's OCR transcription therefore lives <b>both</b> inline in the parent's Markdown and as the seed of its
/// own sub-document. Never delete a Figure row out-of-band: a Spawned one's <c>SegmentKey</c> is the sole
/// duplicate-spawn barrier (#481 moved spawn idempotency entirely onto this ledger), and a still-Pending one is a
/// real constituent whose spawn has not happened yet.
/// </para>
/// </summary>
public enum DocumentSegmentKind
{
    /// <summary>A born-digital text span — a constituent of a container bundle (#346).</summary>
    Text = 0,

    /// <summary>
    /// An embedded-figure OCR span (#306) that the detection pass judged a standalone document of its own — an
    /// invoice or receipt photo, a scanned certificate. In the document Markdown such a span is bracketed by the
    /// in-band <c>*[Image OCR]*…*[End OCR]*</c> provenance markers, and the child seeds from the marker bodies
    /// only (<c>ImageOcrMarkup.ExtractBodies</c>, #373).
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
    /// <see cref="DocumentSegmentKind.Figure"/> is a genuinely embedded document, so it spawns whether or not the
    /// source is a container and its already-spawned sub-document <b>survives</b> a container→concrete
    /// reclassification; a
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
