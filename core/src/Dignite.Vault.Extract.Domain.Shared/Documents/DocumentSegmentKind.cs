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
/// <b><see cref="Figure"/> is legacy-only since #487 Phase A</b>, which retired figure routing: detection skips a
/// figure span before any row is persisted (its OCR transcription stays inline in the parent's Markdown), so no
/// production path writes <see cref="Figure"/> anymore. Rows persisted by pre-#487 deployments remain, with two
/// retention rules: a <b>Spawned</b> Figure row is deliberately kept — its <c>SegmentKey</c> is the sole
/// duplicate-spawn barrier (#481 moved spawn idempotency entirely onto this ledger) and its kind shields its live
/// sub-document from the #364 retraction; a <b>still-Pending</b> Figure row is deleted on encounter by
/// <c>DocumentSegmentationJob</c> instead of spawned.
/// </para>
/// </summary>
public enum DocumentSegmentKind
{
    /// <summary>A born-digital text span — a constituent of a container bundle (#346). The only kind fresh
    /// detection persists since #487.</summary>
    Text = 0,

    /// <summary>
    /// An embedded-figure OCR span (#306) — <b>legacy-only</b>: #487 Phase A retired figure routing, so this value
    /// survives only on rows persisted by pre-#487 deployments (see the enum remarks for their retention rules).
    /// In the document Markdown such a span was bracketed by the in-band <c>*[Image OCR]*…*[End OCR]*</c>
    /// provenance markers.
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
    /// <see cref="DocumentSegmentKind.Figure"/> (legacy rows only since #487) is a genuinely embedded document, so
    /// its already-spawned sub-document <b>survives</b> a container→concrete reclassification; a
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
