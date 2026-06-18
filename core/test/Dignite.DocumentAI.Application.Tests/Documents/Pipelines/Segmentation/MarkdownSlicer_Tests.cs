using System.Collections.Generic;
using Dignite.DocumentAI.Documents.Pipelines.Segmentation;
using Shouldly;
using Xunit;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// Pure-logic tests for <see cref="MarkdownSlicer"/> (#346): the deterministic cutter that turns the LLM's
/// verbatim boundary markers into slices. This is the heart of the born-digital stability decision (the LLM never
/// regenerates content), so its verification + edge cases are covered directly without an ABP host.
/// </summary>
public class MarkdownSlicer_Tests
{
    [Fact]
    public void Splits_Two_Documents_At_Markers()
    {
        const string markdown = "Invoice A first\nInvoice B second";
        var boundaries = new List<SegmentBoundary>
        {
            new("Invoice A", IsSubDocument: true),
            new("Invoice B", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].Text.ShouldBe("Invoice A first");
        slices[0].IsSubDocument.ShouldBeTrue();
        slices[0].Ordinal.ShouldBe(0);
        slices[1].Text.ShouldBe("Invoice B second");
        slices[1].Ordinal.ShouldBe(1);
    }

    [Fact]
    public void Folds_Leading_Preamble_Into_First_Slice()
    {
        // Content before the first marker (a transmittal line the LLM did not mark) must not be dropped.
        const string markdown = "PREAMBLE LINE\nInvoice A body\nInvoice B body";
        var boundaries = new List<SegmentBoundary>
        {
            new("Invoice A", IsSubDocument: true),
            new("Invoice B", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].Text.ShouldContain("PREAMBLE LINE");
        slices[0].Text.ShouldContain("Invoice A body");
        slices[1].Text.ShouldBe("Invoice B body");
    }

    [Fact]
    public void Repeated_Marker_Maps_To_Successive_Occurrences()
    {
        // Two documents whose first lines are identical: the forward-advancing search must map each marker to the
        // next occurrence, not both to the first.
        const string markdown = "Invoice\nfirst\nInvoice\nsecond";
        var boundaries = new List<SegmentBoundary>
        {
            new("Invoice", IsSubDocument: true),
            new("Invoice", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].Text.ShouldBe("Invoice\nfirst");
        slices[1].Text.ShouldBe("Invoice\nsecond");
    }

    [Fact]
    public void Preserves_IsSubDocument_Flag_For_NonDocument_Slices()
    {
        const string markdown = "TABLE OF CONTENTS\nInvoice A body";
        var boundaries = new List<SegmentBoundary>
        {
            new("TABLE OF CONTENTS", IsSubDocument: false),
            new("Invoice A", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].IsSubDocument.ShouldBeFalse();
        slices[1].IsSubDocument.ShouldBeTrue();
    }

    [Fact]
    public void Matches_A_Marker_Only_At_A_Line_Start_Not_A_Mid_Line_Occurrence()
    {
        // "Amount" appears mid-line inside the first slice's body AND at the start of the second slice's line. The
        // cut must bind to the line-start occurrence, not the earlier mid-line one (which would silently mis-slice).
        const string markdown = "Invoice 1 Amount X\nAmount\npaid";
        var boundaries = new List<SegmentBoundary>
        {
            new("Invoice 1", IsSubDocument: true),
            new("Amount", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].Text.ShouldBe("Invoice 1 Amount X"); // full first line; the mid-line "Amount" is not used as the cut
        slices[1].Text.ShouldBe("Amount\npaid");
    }

    [Fact]
    public void Returns_False_When_A_Marker_Is_Not_Found_Verbatim()
    {
        const string markdown = "Invoice A first\nInvoice B second";
        var boundaries = new List<SegmentBoundary>
        {
            new("Invoice A", IsSubDocument: true),
            new("Invoice C never appears", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeFalse();
        slices.ShouldBeEmpty();
    }

    [Fact]
    public void Returns_False_For_Empty_Input_Or_No_Boundaries()
    {
        MarkdownSlicer.TrySlice("", new List<SegmentBoundary> { new("x", true) }, out var s1).ShouldBeFalse();
        s1.ShouldBeEmpty();

        MarkdownSlicer.TrySlice("some text", new List<SegmentBoundary>(), out var s2).ShouldBeFalse();
        s2.ShouldBeEmpty();

        MarkdownSlicer.TrySlice("some text", null, out var s3).ShouldBeFalse();
        s3.ShouldBeEmpty();
    }

    [Fact]
    public void Matches_A_Marker_That_Came_Back_With_PromptBoundary_Lt_Encoding()
    {
        // The LLM reads the Markdown after PromptBoundary.WrapDocument has encoded '<' as "&lt;"; a marker carrying
        // that encoding must still be located against the raw Markdown via the decode fallback.
        const string markdown = "<order> details here\nInvoice B body";
        var boundaries = new List<SegmentBoundary>
        {
            new("&lt;order> details", IsSubDocument: true),
            new("Invoice B", IsSubDocument: true)
        };

        MarkdownSlicer.TrySlice(markdown, boundaries, out var slices).ShouldBeTrue();

        slices.Count.ShouldBe(2);
        slices[0].Text.ShouldContain("<order> details here");
        slices[1].Text.ShouldBe("Invoice B body");
    }
}
