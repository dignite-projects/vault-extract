using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Renders a Word paragraph's inline content to Markdown: run text with <b>bold</b>/<i>italic</i> emphasis
/// and hyperlinks. Used by <see cref="DocxExtractor"/> for body paragraphs (headings are rendered as plain
/// collapsed text; table cells use their own plain renderer).
/// <para>
/// <b>Accepted view of tracked changes.</b> The walk recurses into inserted-revision wrappers
/// (<c>w:ins</c>) so their runs are kept, and skips deleted-revision wrappers (<c>w:del</c>) entirely;
/// run text reads only <c>w:t</c> (deleted text lives in <c>w:delText</c>), matching
/// <c>DocxExtractor.ParagraphText</c>. Other inline containers (<c>w:smartTag</c>, <c>w:fldSimple</c>, …)
/// are unwrapped so their visible run text is not lost.
/// </para>
/// <para>
/// <b>Emphasis hygiene.</b> Consecutive runs with the same bold/italic state are merged so the output is
/// <c>**Hello**</c>, not <c>**Hel****lo**</c> (Word often splits a styled span across several runs). Leading
/// and trailing whitespace is moved <i>outside</i> the emphasis markers (<c>" **word** "</c>, not
/// <c>"** word **"</c>) so the emphasis actually renders in CommonMark.
/// </para>
/// </summary>
internal static class WordParagraphRenderer
{
    public static string Render(W.Paragraph paragraph, MainDocumentPart mainPart)
    {
        var builder = new InlineBuilder(mainPart);
        builder.Walk(paragraph);
        return builder.ToMarkdown();
    }

    /// <summary>
    /// Accumulates inline output, merging consecutive same-format run text before emitting it so emphasis
    /// markers are not fragmented across Word's run splits.
    /// </summary>
    private sealed class InlineBuilder
    {
        private readonly MainDocumentPart _mainPart;
        private readonly StringBuilder _sb = new();
        private string? _pendingText;
        private bool _pendingBold;
        private bool _pendingItalic;

        public InlineBuilder(MainDocumentPart mainPart) => _mainPart = mainPart;

        public void Walk(DocumentFormat.OpenXml.OpenXmlElement container)
        {
            foreach (var child in container.ChildElements)
            {
                switch (child)
                {
                    case W.Run run:
                        AppendRun(run);
                        break;

                    case W.Hyperlink link:
                        // A hyperlink is an emphasis boundary: flush the pending run text first so a styled
                        // run before the link is not merged across it.
                        Flush();
                        _sb.Append(RenderHyperlink(link));
                        break;

                    case W.DeletedRun:
                        // w:del — deleted-revision text, excluded from the accepted view.
                        break;

                    case W.ParagraphProperties:
                        // pPr carries no inline text (style / numbering / outline level).
                        break;

                    default:
                        // Unwrap other inline containers (w:ins inserted runs, w:smartTag, w:fldSimple, …)
                        // so their run text is not lost; w:del is the only revision wrapper we skip.
                        Walk(child);
                        break;
                }
            }
        }

        public string ToMarkdown()
        {
            Flush();
            return _sb.ToString().Trim();
        }

        private void AppendRun(W.Run run)
        {
            var text = RunText(run);
            if (text.Length == 0)
            {
                return;
            }

            var rPr = run.RunProperties;
            var bold = IsOn(rPr?.Bold);
            var italic = IsOn(rPr?.Italic);

            if (_pendingText is not null && bold == _pendingBold && italic == _pendingItalic)
            {
                _pendingText += text;
            }
            else
            {
                Flush();
                _pendingText = text;
                _pendingBold = bold;
                _pendingItalic = italic;
            }
        }

        private void Flush()
        {
            if (_pendingText is { Length: > 0 })
            {
                _sb.Append(Emphasize(_pendingText, _pendingBold, _pendingItalic));
            }

            _pendingText = null;
            _pendingBold = false;
            _pendingItalic = false;
        }

        private string RenderHyperlink(W.Hyperlink link)
        {
            // Hyperlink display text is rendered as plain text (no nested emphasis this step). Reading runs
            // via Descendants keeps text inside an inserted-revision wrapper; deleted text stays in
            // w:delText, which RunText does not read, so it is naturally excluded.
            var text = string.Concat(link.Descendants<W.Run>().Select(RunText)).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            var url = ResolveUrl(link.Id?.Value);
            return url is null ? text : FormatLink(text, url);
        }

        private string? ResolveUrl(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                // An internal anchor (w:anchor, no r:id) has no resolvable URL — render the text only.
                return null;
            }

            var relationship = _mainPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == id);
            return relationship?.Uri?.ToString();
        }
    }

    /// <summary>
    /// Builds a Markdown link, escaping the label so a <c>[</c>/<c>]</c> in the display text cannot close it
    /// early, and using the angle-bracket destination form when the URL contains whitespace or parentheses.
    /// A literal space in the destination — which <c>System.Uri.ToString()</c> can reintroduce by decoding
    /// <c>%20</c> — otherwise makes CommonMark not render the link at all; an unbalanced <c>)</c> would
    /// truncate the destination. The angle-bracket form takes any character except a literal <c>&lt;</c>/<c>&gt;</c>,
    /// which are percent-encoded.
    /// </summary>
    private static string FormatLink(string text, string url)
    {
        var label = text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");
        var destination = NeedsAngleBrackets(url)
            ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">"
            : url;
        return $"[{label}]({destination})";
    }

    private static bool NeedsAngleBrackets(string url)
        => url.Any(c => char.IsWhiteSpace(c) || c == '(' || c == ')');

    private static string RunText(W.Run run)
    {
        var sb = new StringBuilder();
        foreach (var element in run.ChildElements)
        {
            switch (element.LocalName)
            {
                case "t":
                    sb.Append(element.InnerText);
                    break;
                case "tab":
                    sb.Append(' ');
                    break;
                case "br":
                case "cr":
                    sb.Append('\n');
                    break;
            }
        }

        return sb.ToString();
    }

    private static bool IsOn(W.Bold? toggle) => toggle is not null && (toggle.Val is null || toggle.Val.Value);

    private static bool IsOn(W.Italic? toggle) => toggle is not null && (toggle.Val is null || toggle.Val.Value);

    /// <summary>
    /// Wraps <paramref name="text"/> in the emphasis marker for its bold/italic state, with any leading or
    /// trailing whitespace moved outside the marker (CommonMark does not render <c>** word **</c>). Returns
    /// the text unchanged when it has no emphasis or is all whitespace.
    /// </summary>
    private static string Emphasize(string text, bool bold, bool italic)
    {
        if (!bold && !italic)
        {
            return text;
        }

        var core = text.Trim();
        if (core.Length == 0)
        {
            return text;
        }

        var lead = 0;
        while (lead < text.Length && char.IsWhiteSpace(text[lead]))
        {
            lead++;
        }

        var trail = 0;
        while (trail < text.Length - lead && char.IsWhiteSpace(text[text.Length - 1 - trail]))
        {
            trail++;
        }

        var marker = bold && italic ? "***" : bold ? "**" : "*";
        return text.Substring(0, lead) + marker + core + marker + text.Substring(text.Length - trail);
    }
}
