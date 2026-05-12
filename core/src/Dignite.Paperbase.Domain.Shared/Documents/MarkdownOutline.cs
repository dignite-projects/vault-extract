using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 在 Markdown 文本上做轻量结构化检索：抽取标题树（<see cref="Extract"/>）和
/// 关键字 grep 命中段落（<see cref="Grep"/>）。两者都是纯函数，零外部依赖，
/// 设计为给 Chat 工具调用使用（<c>get_document_outline</c> / <c>get_document_excerpt</c>）。
///
/// <para>
/// 与 <see cref="MarkdownStripper"/> 形成互补：Stripper 抹去 Markdown 标记换取纯文本，
/// MarkdownOutline 反而<em>利用</em>标记（# / ##）作为结构信号——保留标题位置和层级
/// 是 LLM 精确导航和回答"第 3 节讲什么"这种问题的关键。
/// </para>
/// </summary>
public static class MarkdownOutline
{
    /// <summary>
    /// 一个 ATX 风格标题节点（<c># Title</c> 到 <c>###### Title</c>）。
    /// <see cref="LineNumber"/> 是 1-based 行号，便于 UI 跳转或调试。
    /// </summary>
    public sealed record HeaderNode(int Level, string Title, int LineNumber);

    // ^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$ — CommonMark 允许 ATX 标题后跟可选的结尾 #，并允许 0-3 空格前导。
    // 不识别 Setext 标题（=== / ---），项目里 Markdown 抽取流水线产出的是 ATX 形式。
    private static readonly Regex HeaderRegex = new(
        @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// 抽取 Markdown 中所有 ATX 标题。空字符串或 null 返回空列表（不抛）。
    /// 单次调用最多返回 <paramref name="maxHeaders"/> 个标题，超出截断（防上下文窗口爆炸）。
    /// </summary>
    public static IReadOnlyList<HeaderNode> Extract(string? markdown, int maxHeaders = 100)
    {
        if (maxHeaders <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeaders));
        if (string.IsNullOrEmpty(markdown)) return Array.Empty<HeaderNode>();

        var results = new List<HeaderNode>();
        var lines = SplitLines(markdown);
        var inFence = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // 围栏代码块内的 # 不是标题（CommonMark 行为）。简单 toggle，不区分 ``` / ~~~。
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var match = HeaderRegex.Match(line);
            if (!match.Success) continue;

            var level = match.Groups[1].Length;
            var title = match.Groups[2].Value.Trim();
            if (title.Length == 0) continue;

            results.Add(new HeaderNode(level, title, i + 1));
            if (results.Count >= maxHeaders) break;
        }

        return results;
    }

    /// <summary>
    /// 在 Markdown 中按子串（大小写不敏感）搜索 <paramref name="query"/>，返回命中段落，
    /// 每段附带前后 <paramref name="contextLines"/> 行上下文。重叠或相邻的上下文窗口合并为
    /// 一个 snippet（避免重复行）。最多返回 <paramref name="maxMatches"/> 个 snippet（防爆窗口）。
    /// 空 query 或空文本返回空列表（不抛）。
    /// </summary>
    public static IReadOnlyList<string> Grep(
        string? markdown, string query,
        int contextLines = 2, int maxMatches = 10)
    {
        if (contextLines < 0) throw new ArgumentOutOfRangeException(nameof(contextLines));
        if (maxMatches <= 0) throw new ArgumentOutOfRangeException(nameof(maxMatches));
        if (string.IsNullOrEmpty(markdown) || string.IsNullOrEmpty(query))
        {
            return Array.Empty<string>();
        }

        var lines = SplitLines(markdown);
        // Pass 1: sweep hits, building merged context windows by interval coalescing.
        // A new hit whose window starts within (or directly after) the previous window
        // extends that window rather than spawning a duplicate. maxMatches caps the number
        // of *distinct* windows, not raw hits — clusters of hits collapse into one entry.
        var windows = new List<(int Start, int End)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

            var start = Math.Max(0, i - contextLines);
            var end = Math.Min(lines.Count - 1, i + contextLines);

            if (windows.Count > 0 && start <= windows[^1].End)
            {
                // Strict overlap (windows share at least one line) — extend the previous
                // window. Adjacent-but-disjoint windows stay separate so two consecutive
                // single-line hits with contextLines=0 still report as two matches.
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, end));
            }
            else
            {
                if (windows.Count >= maxMatches) break;
                windows.Add((start, end));
            }
        }

        if (windows.Count == 0) return Array.Empty<string>();

        var results = new List<string>(windows.Count);
        foreach (var (s, e) in windows)
        {
            results.Add(string.Join('\n', SliceRange(lines, s, e)));
        }
        return results;
    }

    private static List<string> SplitLines(string text)
    {
        // Preserve empty lines so the line numbers stay aligned with the source.
        return new List<string>(text.Split('\n'));
    }

    private static IEnumerable<string> SliceRange(List<string> lines, int startInclusive, int endInclusive)
    {
        for (var i = startInclusive; i <= endInclusive; i++)
        {
            yield return lines[i].TrimEnd('\r');
        }
    }
}
