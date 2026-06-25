using System;
using System.Collections.Generic;
using Dignite.Vault.Extract.Abstractions.Parse;

namespace Dignite.Vault.Extract.Documents.Pipelines.Segmentation;

/// <summary>
/// A span boundary proposed by the unified sub-document detection pass (#346/#371): a <b>verbatim</b> start marker
/// copied from the source Markdown (the <c>*[Image OCR]*</c> marker line for a figure span) plus whether the span it
/// opens is itself a standalone sub-document (vs the parent's own content / a cover / an element of the parent).
/// </summary>
public sealed record SegmentBoundary(string StartMarker, bool IsSubDocument);

/// <summary>
/// A deterministically-cut span of a source document's Markdown (#346/#371). <see cref="IsFigure"/> is the span's
/// STRUCTURAL kind — true when its opening boundary marker is an <c>*[Image OCR]*</c> marker line (the span IS a
/// figure), false for a text span even when it embeds an inline figure block (#371: kind comes from the boundary,
/// not from scanning the body).
/// </summary>
public sealed record MarkdownSlice(string Text, bool IsSubDocument, int Ordinal, bool IsFigure);

/// <summary>
/// Deterministically cuts a container's Markdown at LLM-proposed boundaries (#346 decision: the LLM returns
/// <b>verbatim start markers</b>, never regenerated slice text; the code does the cutting, so there is no content
/// drift and the result is machine-verifiable). This is the highest-leverage stability lever of the born-digital
/// path: the LLM's unreliable job (boundary judgment) is bounded to short markers, while exact content is owned by
/// code.
/// </summary>
public static class MarkdownSlicer
{
    /// <summary>
    /// Cuts <paramref name="markdown"/> at <paramref name="boundaries"/>. Each marker is located by an ordinal
    /// forward search with an advancing cursor, so repeated markers (e.g. two invoices both starting "Invoice")
    /// map to successive occurrences in document order. Slices run marker-to-marker (last to end); any leading
    /// preamble before the first marker is folded into the first slice so no content is dropped.
    /// <para>
    /// Returns <c>false</c> (with an empty <paramref name="slices"/>) when the boundaries cannot be trusted —
    /// empty input, or a marker not found verbatim at or after the previous marker's position — so the caller raises
    /// a review signal instead of spawning garbage. (Out-of-order markers are rejected by that same forward search,
    /// not a separate ordering pass: a marker that appears only before its predecessor is not found from the
    /// advancing cursor and yields <c>false</c>.) A marker is matched against the raw Markdown first, then against a copy with
    /// <c>&amp;lt;</c> decoded back to <c>&lt;</c>, because the LLM reads the Markdown after
    /// <see cref="Dignite.Vault.Extract.Ai.PromptBoundary.WrapDocument"/> has encoded <c>&lt;</c>.
    /// </para>
    /// </summary>
    public static bool TrySlice(
        string? markdown,
        IReadOnlyList<SegmentBoundary>? boundaries,
        out List<MarkdownSlice> slices)
    {
        slices = new List<MarkdownSlice>();

        if (string.IsNullOrEmpty(markdown) || boundaries is not { Count: > 0 })
        {
            return false;
        }

        var positions = new int[boundaries.Count];
        var cursor = 0;
        for (var i = 0; i < boundaries.Count; i++)
        {
            var pos = FindMarker(markdown, boundaries[i].StartMarker, cursor);
            if (pos < 0)
            {
                return false; // marker not found verbatim -> untrusted split
            }

            positions[i] = pos;
            cursor = pos + 1; // advance so a repeated marker maps to the next occurrence
        }

        for (var i = 0; i < boundaries.Count; i++)
        {
            // The first slice folds in any leading preamble (content before the first marker) so nothing is lost.
            var start = i == 0 ? 0 : positions[i];
            var end = i == boundaries.Count - 1 ? markdown.Length : positions[i + 1];

            var text = markdown[start..end].Trim();
            if (text.Length == 0)
            {
                continue; // defensively skip an empty slice (e.g. adjacent markers)
            }

            // #371: the span's figure-ness is a structural property of the boundary the LLM opened it with — an
            // *[Image OCR]* marker line means the span IS a figure; a prose first line means a text span (even if
            // it embeds an inline figure block). Carry it here rather than scanning the slice body downstream.
            slices.Add(new MarkdownSlice(
                text, boundaries[i].IsSubDocument, slices.Count, ImageOcrMarkup.IsOpenLine(boundaries[i].StartMarker)));
        }

        return slices.Count > 0;
    }

    private static int FindMarker(string markdown, string? marker, int cursor)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return -1;
        }

        var pos = FindLineStart(markdown, marker, cursor);
        if (pos >= 0)
        {
            return pos;
        }

        // The LLM read the Markdown through PromptBoundary.WrapDocument, which encodes '<' as "&lt;". If the marker
        // came back carrying that encoding, decode it before retrying against the raw Markdown.
        if (marker.Contains("&lt;", StringComparison.Ordinal))
        {
            return FindLineStart(markdown, marker.Replace("&lt;", "<", StringComparison.Ordinal), cursor);
        }

        return -1;
    }

    // The LLM is asked to return each constituent's FIRST LINE as the marker, so a marker is only a valid cut point
    // at the START of a line (offset 0 or immediately after a newline). Anchoring to a line start prevents a short
    // marker (e.g. "Total") from binding to an earlier mid-line occurrence inside the previous slice's body, which
    // would silently mis-cut the document while still "finding" the marker verbatim.
    private static int FindLineStart(string markdown, string marker, int cursor)
    {
        var from = cursor;
        while (true)
        {
            var pos = markdown.IndexOf(marker, from, StringComparison.Ordinal);
            if (pos < 0)
            {
                return -1;
            }

            if (pos == 0 || markdown[pos - 1] == '\n')
            {
                return pos;
            }

            from = pos + 1; // this occurrence is mid-line; keep looking for a line-anchored one
        }
    }
}
