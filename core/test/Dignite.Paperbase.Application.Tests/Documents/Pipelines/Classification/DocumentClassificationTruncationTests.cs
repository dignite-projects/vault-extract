using Dignite.Paperbase.Documents.Pipelines.Classification;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 回归保护：<see cref="DocumentClassificationWorkflow.TruncateAtCharBoundary"/> 不切断代理对。
///
/// 背景：分类按 MaxTextLengthPerExtraction（UTF-16 码元）截断文档前部喂 LLM。朴素的
/// <c>markdown[..N]</c> 会在 N 落在代理对中间时留下半个码点——UTF-8 编码送 LLM 时退化成 U+FFFD。
/// 截断点位于被丢弃的尾部，故末位若是高位代理则一并丢弃即可（多退一个 char 无影响）。
/// </summary>
public class DocumentClassificationTruncationTests
{
    [Fact]
    public void Returns_Text_Unchanged_When_Within_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("hello", 10).ShouldBe("hello");
        DocumentClassificationWorkflow.TruncateAtCharBoundary("hello", 5).ShouldBe("hello");
    }

    [Fact]
    public void Truncates_Plain_Text_At_Char_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("abcdef", 3).ShouldBe("abc");
    }

    [Fact]
    public void Does_Not_Split_A_Surrogate_Pair_At_The_Cut()
    {
        // "A" + 😀 (U+1F600，一个代理对 = 2 个 UTF-16 码元) + "B"。
        // 在 maxChars=2 处截断会落在代理对中间——应丢弃整个高位代理，得到 "A"（长度 1），不留半个码点。
        var text = "A\U0001F600B";
        var result = DocumentClassificationWorkflow.TruncateAtCharBoundary(text, 2);

        result.ShouldBe("A");
        result.Length.ShouldBe(1);
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
    }

    [Fact]
    public void Keeps_A_Whole_Surrogate_Pair_When_It_Fits()
    {
        // maxChars=3 容纳 "A" + 完整代理对 → 原样保留（长度 3）。
        var text = "A\U0001F600B";
        var result = DocumentClassificationWorkflow.TruncateAtCharBoundary(text, 3);

        result.ShouldBe("A\U0001F600");
        result.Length.ShouldBe(3);
    }

    [Fact]
    public void Returns_Empty_For_Non_Positive_Limit()
    {
        DocumentClassificationWorkflow.TruncateAtCharBoundary("abc", 0).ShouldBe(string.Empty);
    }
}
