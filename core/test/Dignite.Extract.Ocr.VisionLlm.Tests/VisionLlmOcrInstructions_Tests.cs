using Shouldly;
using Xunit;

namespace Dignite.Extract.Ocr.VisionLlm;

/// <summary>
/// #383 / #409: guards the running-header/footer + page-number exclusion in the OCR system prompt, and — more
/// importantly — that it is paired with the explicit protection of the body content the exclusion must never
/// eat: page-bottom content (signatures, seals, totals, footnotes) and, per #409, the document's own
/// title/headings at the top edge (a one-off masthead is not a running header). #409 also pins the
/// heading-level guidance that lets the model render visual hierarchy. The prompt is the only lever on the
/// VisionLlm path (single-page OCR cannot do cross-page detection), so these clauses must not be silently dropped.
/// </summary>
public class VisionLlmOcrInstructions_Tests
{
    [Fact]
    public void System_prompt_excludes_running_headers_footers_and_page_numbers()
    {
        var prompt = VisionLlmOcrInstructions.SystemPrompt;

        prompt.ShouldContain("running page headers");
        prompt.ShouldContain("running page footers");
        prompt.ShouldContain("page numbers");
        // #409: the exclusion must be framed as *repeated* (every-page) boilerplate, so it cannot be read as
        // "drop the top line" and eat a one-off document title.
        prompt.ShouldContain("edge of every page");
    }

    [Fact]
    public void System_prompt_protects_page_bottom_body_content()
    {
        var prompt = VisionLlmOcrInstructions.SystemPrompt;

        // The carve-out that keeps the exclusion from sacrificing real content at the bottom of the page.
        prompt.ShouldContain("NEVER drop");
        prompt.ShouldContain("signature");
        prompt.ShouldContain("total");
        prompt.ShouldContain("footnotes");
    }

    [Fact]
    public void System_prompt_protects_the_document_title_and_headings_at_the_top_edge()
    {
        var prompt = VisionLlmOcrInstructions.SystemPrompt;

        // #409: a masthead title (e.g. 請求書 centered at the top of an invoice) must survive the running-header
        // exclusion and be transcribed as a heading, not dropped as page chrome.
        prompt.ShouldContain("NEVER drop the document title or any heading");
        prompt.ShouldContain("very top edge of the page");
    }

    [Fact]
    public void System_prompt_guides_the_model_to_assign_heading_levels()
    {
        var prompt = VisionLlmOcrInstructions.SystemPrompt;

        // #409: let the model decide structure — but tell it to use heading levels reflecting visual hierarchy,
        // with the main title as the top-level heading (the gap that left scanned-form titles as plain text).
        prompt.ShouldContain("heading levels");
        prompt.ShouldContain("#, ##, ###");
        prompt.ShouldContain("top-level heading");
    }
}
