using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Dignite.Extract.Abstractions.Parse;

/// <summary>
/// In-band <b>provenance annotations</b> that bracket an embedded-figure OCR transcription inlined into the Markdown
/// (#371, unifying figure routing #306/#365 and born-digital segmentation #346 into one Markdown-borne sub-document
/// pass; MarkItDown's <c>*[Image OCR]*…*[End OCR]*</c> is the prior art and the exact format we adopt). A figure's
/// transcription is already inlined at its reading position (#301); these markers <b>delimit</b> it and tell any
/// consumer — human, RAG engine, LLM — that the bracketed text came from OCR of an embedded image.
/// <para>
/// <b>Provenance annotation, not a pipeline control signal (#381).</b> The markers stay in <c>Document.Markdown</c>,
/// the persisted egress payload — they are no longer stripped before persistence. This aligns Dignite Extract with
/// MarkItDown's philosophy (the markers are part of the final Markdown), dissolves the salt that #376 needed only to
/// keep a stripped egress safe from re-ingested MarkItDown content, and makes a re-ingested MarkItDown document fall
/// out naturally: its <c>*[Image OCR]*</c> blocks are already provenance-annotated content and need no special-casing.
/// The pipeline still reads the markers as structure — <see cref="Contains"/> is a (now demoted) recall signal for
/// figure routing, and the unified sub-document pass uses <see cref="IsOpenLine"/> / <see cref="ExtractBodies"/> /
/// <see cref="Strip"/> to derive a spawned child's seed text — but the markers themselves travel all the way to the
/// egress, not just to an internal artifact.
/// </para>
/// <para>
/// Each marker is a line of its own. The open line optionally carries the 1-based source page
/// (<c>*[Image OCR p:3]*</c>) as a lightweight recovery anchor (#371 / #210: a position is provenance, never
/// identity — identity is the span content hash); the bare <c>*[Image OCR]*</c> form (MarkItDown's own string, and a
/// page-less figure) is equally recognized. Only compile-time constants are emitted (no runtime user-string
/// concatenation, per the LLM security conventions); the page number is the sole interpolated value and is an
/// <see cref="int"/>.
/// </para>
/// </summary>
public static class ImageOcrMarkup
{
    /// <summary>The open marker with no page anchor (<c>*[Image OCR]*</c>) — byte-identical to MarkItDown's own.</summary>
    public const string OpenMarker = "*[Image OCR]*";

    /// <summary>Prefix of a page-anchored open marker; the full line is <c>*[Image OCR p:{page}]*</c>.</summary>
    public const string OpenPagePrefix = "*[Image OCR p:";

    /// <summary>Suffix that closes a page-anchored open marker (the italic <c>*</c> after the <c>]</c>).</summary>
    private const string PageSuffix = "]*";

    /// <summary>The close marker (<c>*[End OCR]*</c>) — byte-identical to MarkItDown's own.</summary>
    public const string CloseMarker = "*[End OCR]*";

    /// <summary>
    /// Wraps a figure's OCR <paramref name="transcription"/> in the open/close markers, each on its own line,
    /// with an optional 1-based <paramref name="pageNumber"/> anchor on the open line. A non-positive / null page
    /// produces the bare open marker.
    /// </summary>
    public static string Wrap(string? transcription, int? pageNumber)
    {
        var open = pageNumber is { } p && p > 0
            ? OpenPagePrefix + p.ToString(CultureInfo.InvariantCulture) + PageSuffix
            : OpenMarker;
        return open + "\n" + (transcription ?? string.Empty) + "\n" + CloseMarker;
    }

    /// <summary>
    /// Whether <paramref name="markdown"/> contains at least one figure open marker <b>on its own line</b> — the
    /// cheap structural "has figures" signal. Since #381 this is a <b>demoted</b> recall hint (figure / sub-document
    /// spawning is decided by LLM semantic judgment, not gated on this), still used as the zero-cost deterministic
    /// trigger that lets the focused segmentation pass look at a figure-bearing document. Whole-line matching
    /// (consistent with <see cref="Strip"/>) so ordinary content that merely <i>mentions</i> <c>*[Image OCR]*</c>
    /// inside a longer line is not mistaken for a figure span.
    /// </summary>
    public static bool Contains(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return false;
        }

        foreach (var line in markdown.Split('\n'))
        {
            if (IsOpenLine(line))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Removes every marker <b>line</b> (open or close), leaving the transcription content in place — the exact
    /// inverse of <see cref="Wrap"/>. A line is treated as a marker only when the marker is the whole trimmed line,
    /// so ordinary prose that merely mentions the label is untouched.
    /// <para>
    /// Since #381 this is <b>no longer applied to the egress</b> (<c>Document.Markdown</c> keeps the markers). It
    /// survives as a content utility for the unified sub-document pass, which uses it to derive a spawned text
    /// child's clean seed (stripping the inline figure markers a folded text span carried).
    /// </para>
    /// </summary>
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !Contains(markdown))
        {
            return markdown ?? string.Empty;
        }

        return string.Join("\n", markdown.Split('\n').Where(line => !IsSentinelLine(line)));
    }

    /// <summary>
    /// Returns the concatenated figure transcription <b>body</b> — the content between each
    /// <c>*[Image OCR]*…*[End OCR]*</c> pair, joined by newlines — with everything OUTSIDE the markers (and the
    /// marker lines themselves) dropped. Used (#371/#373) to spawn a figure sub-document from ONLY its figure body,
    /// so any surrounding parent text the LLM folded into the span (e.g. by omitting a separate parent-body boundary)
    /// is excluded; the figure child is the transcription, nothing more. Returns the empty string when there is no
    /// figure block. An unclosed open marker keeps the rest as body (fail-open, never drops content silently).
    /// </summary>
    public static string ExtractBodies(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var inside = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (IsOpenLine(line))
            {
                inside = true;
                continue;
            }

            if (IsCloseLine(line))
            {
                inside = false;
                continue;
            }

            if (inside)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>Whether the (trimmed) line is a figure open marker (bare or page-anchored).</summary>
    public static bool IsOpenLine(string? line)
    {
        if (line is null)
        {
            return false;
        }

        var trimmed = line.Trim();
        return trimmed == OpenMarker || IsPageOpen(trimmed);
    }

    /// <summary>Whether the (trimmed) line is the figure close marker.</summary>
    public static bool IsCloseLine(string? line)
        => line is not null && line.Trim() == CloseMarker;

    /// <summary>
    /// Parses the 1-based page from a page-anchored open marker line (<c>*[Image OCR p:3]*</c>), or <c>null</c>
    /// for a bare open line / any non-open line.
    /// </summary>
    public static int? TryParsePage(string? line)
    {
        if (line is null)
        {
            return null;
        }

        var trimmed = line.Trim();
        if (!IsPageOpen(trimmed))
        {
            return null;
        }

        var body = trimmed.Substring(OpenPagePrefix.Length, trimmed.Length - OpenPagePrefix.Length - PageSuffix.Length);
        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : null;
    }

    private static bool IsSentinelLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed == CloseMarker || trimmed == OpenMarker || IsPageOpen(trimmed);
    }

    /// <summary>Whether the already-trimmed text is a well-formed <c>*[Image OCR p:{digits}]*</c> open line.</summary>
    private static bool IsPageOpen(string trimmed)
    {
        if (!trimmed.StartsWith(OpenPagePrefix, StringComparison.Ordinal)
            || !trimmed.EndsWith(PageSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var bodyLength = trimmed.Length - OpenPagePrefix.Length - PageSuffix.Length;
        if (bodyLength <= 0)
        {
            return false;
        }

        for (var i = OpenPagePrefix.Length; i < trimmed.Length - PageSuffix.Length; i++)
        {
            if (trimmed[i] < '0' || trimmed[i] > '9')
            {
                return false;
            }
        }

        return true;
    }
}
