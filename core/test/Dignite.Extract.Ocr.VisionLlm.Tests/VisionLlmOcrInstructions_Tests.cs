using Shouldly;
using Xunit;

namespace Dignite.Extract.Ocr.VisionLlm;

/// <summary>
/// #383: guards the running header/footer + page-number exclusion in the OCR system prompt, and — more
/// importantly — that it is paired with the explicit protection of page-bottom body content (signatures,
/// seals, totals, footnotes). The prompt is the only lever on the VisionLlm path (single-page OCR cannot do
/// cross-page detection), so the safety clause must not be silently dropped.
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
}
