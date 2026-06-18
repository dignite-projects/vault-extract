using System;
using System.Globalization;
using System.Linq;

namespace Dignite.DocumentAI.Abstractions.TextExtraction;

/// <summary>
/// In-band markers that bracket an embedded-figure OCR transcription inlined into the working Markdown (#371,
/// unifying figure routing #306/#365 and born-digital segmentation #346 into one Markdown-borne sub-document
/// pass; MarkItDown's <c>[Image OCR]…[End OCR]</c> is the prior art). A figure's transcription is already inlined
/// at its reading position (#301); these sentinels only <b>delimit</b> it so two pipeline-internal consumers can
/// recognize a figure span — the classification "contains an embedded standalone document?" signal, and the
/// unified sub-document detection pass.
/// <para>
/// <b>Pipeline-internal only — never reaches the egress.</b> The sentinels are <see cref="Strip"/>-ped before the
/// Markdown becomes the persisted egress payload (<c>Document.Markdown</c>), so the channel text stays clean
/// (Markdown-first). The marked copy is kept only as an internal pipeline artifact for the consumers above.
/// <see cref="Strip"/> is the exact inverse of <see cref="Wrap"/>, so a stripped document equals the pre-#371
/// inline output byte for byte.
/// </para>
/// <para>
/// Each sentinel is a line of its own. The open line optionally carries the 1-based source page
/// (<c>[Image OCR p:3]</c>) as a lightweight recovery anchor (#371 / #210: a position is provenance, never
/// identity — identity is the span content hash). Only compile-time constants are emitted (no runtime
/// user-string concatenation, per the LLM security conventions); the page number is the sole interpolated value
/// and is an <see cref="int"/>.
/// </para>
/// </summary>
public static class ImageOcrMarkup
{
    /// <summary>The open sentinel with no page anchor.</summary>
    public const string OpenMarker = "[Image OCR]";

    /// <summary>Prefix of a page-anchored open sentinel; the full line is <c>[Image OCR p:{page}]</c>.</summary>
    public const string OpenPagePrefix = "[Image OCR p:";

    /// <summary>The close sentinel.</summary>
    public const string CloseMarker = "[End OCR]";

    /// <summary>
    /// Wraps a figure's OCR <paramref name="transcription"/> in the open/close sentinels, each on its own line,
    /// with an optional 1-based <paramref name="pageNumber"/> anchor on the open line. A non-positive / null page
    /// produces the bare open marker.
    /// </summary>
    public static string Wrap(string? transcription, int? pageNumber)
    {
        var open = pageNumber is { } p && p > 0
            ? OpenPagePrefix + p.ToString(CultureInfo.InvariantCulture) + "]"
            : OpenMarker;
        return open + "\n" + (transcription ?? string.Empty) + "\n" + CloseMarker;
    }

    /// <summary>
    /// Whether <paramref name="markdown"/> contains at least one figure open sentinel <b>on its own line</b> — the
    /// cheap structural "has figures" signal (the trigger fallback, the marked-artifact archive gate, and the
    /// per-span Figure/Text kind decision). Whole-line matching (consistent with <see cref="Strip"/>) so ordinary
    /// content that merely <i>mentions</i> <c>[Image OCR]</c> inside a longer line is not mistaken for a figure span.
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
    /// Removes every sentinel <b>line</b> (open or close), leaving the transcription content in place — the exact
    /// inverse of <see cref="Wrap"/>. A line is treated as a sentinel only when the sentinel is the whole trimmed
    /// line, so ordinary prose that merely mentions the label is untouched.
    /// </summary>
    public static string Strip(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !Contains(markdown))
        {
            return markdown ?? string.Empty;
        }

        return string.Join("\n", markdown.Split('\n').Where(line => !IsSentinelLine(line)));
    }

    /// <summary>Whether the (trimmed) line is a figure open sentinel (bare or page-anchored).</summary>
    public static bool IsOpenLine(string? line)
    {
        if (line is null)
        {
            return false;
        }

        var trimmed = line.Trim();
        return trimmed == OpenMarker || IsPageOpen(trimmed);
    }

    /// <summary>Whether the (trimmed) line is the figure close sentinel.</summary>
    public static bool IsCloseLine(string? line)
        => line is not null && line.Trim() == CloseMarker;

    /// <summary>
    /// Parses the 1-based page from a page-anchored open sentinel line (<c>[Image OCR p:3]</c>), or <c>null</c>
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

        var body = trimmed.Substring(OpenPagePrefix.Length, trimmed.Length - OpenPagePrefix.Length - 1);
        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var page) ? page : null;
    }

    private static bool IsSentinelLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed == CloseMarker || trimmed == OpenMarker || IsPageOpen(trimmed);
    }

    /// <summary>Whether the already-trimmed text is a well-formed <c>[Image OCR p:{digits}]</c> open line.</summary>
    private static bool IsPageOpen(string trimmed)
    {
        if (!trimmed.StartsWith(OpenPagePrefix, StringComparison.Ordinal)
            || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return false;
        }

        var bodyLength = trimmed.Length - OpenPagePrefix.Length - 1;
        if (bodyLength <= 0)
        {
            return false;
        }

        for (var i = OpenPagePrefix.Length; i < trimmed.Length - 1; i++)
        {
            if (trimmed[i] < '0' || trimmed[i] > '9')
            {
                return false;
            }
        }

        return true;
    }
}
