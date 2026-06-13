using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Dignite.DocumentAI.TextExtraction.OpenXml;

/// <summary>
/// Renders a chart's <b>backing data</b> (categories + series values cached in the <see cref="ChartPart"/>
/// XML) as a Markdown table. This is <b>pure structured extraction from the format</b> — no OCR, no
/// vision, no LLM, and no vector blind spot — and is explicitly distinct from the deferred
/// "chart → structured via VLM" (#299 decision log).
/// <para>
/// Parsing is by element <c>LocalName</c> rather than strong types so a single pass covers the common
/// category/value chart families (bar / column / line / pie / area) regardless of the specific
/// <c>c:barChart</c> / <c>c:lineChart</c> wrapper. Chart families without a category/value cache
/// (scatter / bubble, which use <c>xVal</c>/<c>yVal</c>) yield no rows here and the caller treats the
/// chart as unrendered (completeness signal), matching the "skip what we can't faithfully extract" stance.
/// </para>
/// </summary>
internal static class ChartRenderer
{
    /// <returns>A Markdown table (optionally preceded by the chart title), or <c>null</c> when no
    /// category/value data could be read.</returns>
    public static string? Render(ChartPart chartPart)
    {
        var chartSpace = chartPart.ChartSpace;
        if (chartSpace is null)
        {
            return null;
        }

        var seriesElements = chartSpace
            .Descendants<OpenXmlElement>()
            .Where(e => e.LocalName == "ser")
            .ToList();
        if (seriesElements.Count == 0)
        {
            return null;
        }

        var series = new List<(string Name, IReadOnlyDictionary<int, string> Values)>();
        var categories = new SortedDictionary<int, string>();

        foreach (var ser in seriesElements)
        {
            var name = ReadSeriesName(ser, series.Count + 1);
            var values = ReadCache(FirstChildByLocalName(ser, "val"));
            if (values.Count == 0)
            {
                // Series carries no value cache (e.g. scatter xVal/yVal, or an empty series). Skip it.
                continue;
            }

            series.Add((name, values));

            // Merge each series' category labels into one shared axis, validating the shared-axis
            // assumption as we go:
            //  - a new index → add it (union: a later series may carry labels at indices an earlier one
            //    lacked, so we must not drop them — that would degrade the row to a numeric fallback);
            //  - same index, an established blank label vs a real one → upgrade to the real label (a cached
            //    blank category must not veto a later series' real labels);
            //  - same index, two DIFFERENT non-blank labels → the series do not share one axis, so a single
            //    wide table would silently hang values under the wrong category. Bail to null; the caller
            //    counts it as a chart failure (#268) — an honest "could not be rendered as a table" signal
            //    instead of misaligned data.
            foreach (var (idx, label) in ReadCache(FirstChildByLocalName(ser, "cat")))
            {
                if (!categories.TryGetValue(idx, out var existing))
                {
                    categories[idx] = label;
                }
                else if (existing.Length > 0 && label.Length > 0 && existing != label)
                {
                    return null;
                }
                else if (existing.Length == 0 && label.Length > 0)
                {
                    categories[idx] = label;
                }
            }
        }

        if (series.Count == 0)
        {
            return null;
        }

        // Row index set: the union of category indices and every series' value indices, so a series with
        // more points than there are category labels still renders all its rows.
        var rowIndices = new SortedSet<int>(categories.Keys);
        foreach (var (_, values) in series)
        {
            foreach (var idx in values.Keys)
            {
                rowIndices.Add(idx);
            }
        }

        var sb = new StringBuilder();

        var title = ReadTitle(chartSpace);
        if (!string.IsNullOrWhiteSpace(title))
        {
            // Collapse newlines so a multi-line / pipe-bearing title cannot break the bold run or the
            // table header directly below it.
            sb.Append("**").Append(MarkdownCell.Inline(title)).Append("**\n\n");
        }

        // Header: leading category column + one column per series.
        sb.Append("| Category | ").Append(string.Join(" | ", series.Select(s => MarkdownCell.Escape(s.Name)))).Append(" |\n");
        sb.Append("| --- |").Append(string.Concat(series.Select(_ => " --- |"))).Append('\n');

        foreach (var idx in rowIndices)
        {
            var label = categories.TryGetValue(idx, out var cat) && !string.IsNullOrWhiteSpace(cat)
                ? cat
                : (idx + 1).ToString();
            sb.Append("| ").Append(MarkdownCell.Escape(label)).Append(" | ");
            sb.Append(string.Join(" | ", series.Select(s =>
                s.Values.TryGetValue(idx, out var v) ? MarkdownCell.Escape(v) : string.Empty)));
            sb.Append(" |\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Series display name from its <c>c:tx</c> string cache, or a positional fallback.</summary>
    private static string ReadSeriesName(OpenXmlElement series, int ordinal)
    {
        var tx = FirstChildByLocalName(series, "tx");
        var value = tx?.Descendants<OpenXmlElement>().FirstOrDefault(e => e.LocalName == "v")?.InnerText;
        return string.IsNullOrWhiteSpace(value) ? $"Series {ordinal}" : value.Trim();
    }

    /// <summary>
    /// Chart title text from the <c>c:chart/c:title</c> rich-text runs, if any. Scoped to the chart's own
    /// direct title child — NOT any descendant <c>title</c> — so a deleted chart title does not get
    /// replaced by an <b>axis</b> title (<c>c:catAx/c:title</c> / <c>c:valAx/c:title</c>).
    /// </summary>
    private static string? ReadTitle(OpenXmlElement chartSpace)
    {
        var chart = FirstChildByLocalName(chartSpace, "chart");
        var title = chart is null ? null : FirstChildByLocalName(chart, "title");
        if (title is null)
        {
            return null;
        }

        // Drawing text runs (a:t) hold the displayed title characters.
        var text = string.Concat(title.Descendants<OpenXmlElement>()
            .Where(e => e.LocalName == "t")
            .Select(e => e.InnerText));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Reads a <c>c:cat</c> / <c>c:val</c> container's cached points into an index → text map. Uses the
    /// number/string cache (<c>c:numCache</c> / <c>c:strCache</c>) so the rendered table reflects what the
    /// deck author saw, without resolving external workbook references. A <c>c:pt</c> whose <c>idx</c> is
    /// absent (the schema default is the next position, and some generators omit it) falls back to the
    /// running position rather than being dropped — otherwise that data point silently disappears and
    /// shifts the category/value alignment.
    /// </summary>
    private static IReadOnlyDictionary<int, string> ReadCache(OpenXmlElement? container)
    {
        var result = new Dictionary<int, string>();
        if (container is null)
        {
            return result;
        }

        var nextIndex = 0;
        foreach (var pt in container.Descendants<OpenXmlElement>().Where(e => e.LocalName == "pt"))
        {
            var idxAttr = pt.GetAttributes().FirstOrDefault(a => a.LocalName == "idx");
            var idx = int.TryParse(idxAttr.Value, out var parsed) ? parsed : nextIndex;
            nextIndex = idx + 1;

            // c:v is a direct child of c:pt — read the child, not the whole subtree.
            var value = pt.ChildElements.FirstOrDefault(e => e.LocalName == "v")?.InnerText ?? string.Empty;
            result[idx] = value.Trim();
        }

        return result;
    }

    private static OpenXmlElement? FirstChildByLocalName(OpenXmlElement parent, string localName)
        => parent.ChildElements.FirstOrDefault(e => e.LocalName == localName);
}
