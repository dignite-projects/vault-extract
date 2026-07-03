using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Shouldly;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Unit tests for <see cref="WordStyleMap.HeadingLevel"/> — the structural backbone of the DOCX rebuild.
/// These cover in-scope production branches that the DocxFixtures package cannot reach via <c>w:pStyle</c>
/// alone (the <c>w:outlineLvl</c> fallback) plus the clamp and the linked-character-style guard, by calling
/// the internal static method directly (the same approach <c>DocxIncompleteReason_Tests</c> uses).
/// </summary>
public class WordStyleMap_Tests
{
    private static W.Paragraph WithStyle(string styleId)
        => new(new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }));

    private static W.Paragraph WithOutline(int level)
        => new(new W.ParagraphProperties(new W.OutlineLevel { Val = level }));

    [Fact]
    public void Maps_heading_style_ids_to_levels()
    {
        WordStyleMap.HeadingLevel(WithStyle("Heading1")).ShouldBe(1);
        WordStyleMap.HeadingLevel(WithStyle("Heading2")).ShouldBe(2);
        WordStyleMap.HeadingLevel(WithStyle("heading3")).ShouldBe(3); // case-insensitive
    }

    [Fact]
    public void Maps_title_style_to_top_level()
    {
        WordStyleMap.HeadingLevel(WithStyle("Title")).ShouldBe(1);
    }

    [Fact]
    public void Clamps_heading_levels_above_six_to_six()
    {
        // Word allows nine heading levels; ATX Markdown allows six.
        WordStyleMap.HeadingLevel(WithStyle("Heading7")).ShouldBe(6);
        WordStyleMap.HeadingLevel(WithStyle("Heading9")).ShouldBe(6);
    }

    [Fact]
    public void Does_not_treat_a_linked_character_style_as_a_heading()
    {
        // "Heading1Char" is the auto-generated linked character style, not a paragraph heading.
        WordStyleMap.HeadingLevel(WithStyle("Heading1Char")).ShouldBeNull();
    }

    [Fact]
    public void Falls_back_to_outline_level_when_there_is_no_heading_style()
    {
        WordStyleMap.HeadingLevel(WithOutline(0)).ShouldBe(1);  // 0-based: level 0 -> H1
        WordStyleMap.HeadingLevel(WithOutline(2)).ShouldBe(3);
        WordStyleMap.HeadingLevel(WithOutline(8)).ShouldBe(6);  // 8 + 1 = 9, clamped to 6
    }

    [Fact]
    public void Outline_level_nine_is_body_text_not_a_heading()
    {
        WordStyleMap.HeadingLevel(WithOutline(9)).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_a_plain_paragraph()
    {
        WordStyleMap.HeadingLevel(new W.Paragraph()).ShouldBeNull();
        WordStyleMap.HeadingLevel(WithStyle("Normal")).ShouldBeNull();
    }

    // Builds an in-memory DOCX carrying the given styles (the inner XML of <w:styles>) plus a standalone
    // paragraph using paragraphStyleId, so HeadingLevel can resolve a custom style against the
    // StyleDefinitionsPart (#316). Dispose the returned document in the caller's using-scope.
    private static (WordprocessingDocument Document, MainDocumentPart MainPart, W.Paragraph Paragraph) BuildWithStyles(
        string paragraphStyleId, string stylesInnerXml)
    {
        var document = WordprocessingDocument.Create(new MemoryStream(), WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body());
        mainPart.AddNewPart<StyleDefinitionsPart>().Styles = new W.Styles(
            $"<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">{stylesInnerXml}</w:styles>");
        var paragraph = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = paragraphStyleId }));
        return (document, mainPart, paragraph);
    }

    [Fact]
    public void Resolves_a_custom_style_based_on_a_builtin_heading()
    {
        // #316: the paragraph uses a custom style whose w:basedOn points at Heading1. The paragraph itself
        // carries no built-in style id and no direct outline, so it is a heading only if HeadingLevel follows
        // the basedOn chain in styles.xml. (Heading1 need not be defined — the id itself carries the level.)
        var (document, mainPart, paragraph) = BuildWithStyles(
            "ChapterTitle",
            "<w:style w:type=\"paragraph\" w:styleId=\"ChapterTitle\"><w:basedOn w:val=\"Heading1\"/></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBe(1);
        }
    }

    [Fact]
    public void Resolves_a_custom_style_with_a_style_level_outline()
    {
        // #316: the custom style's definition carries w:outlineLvl (not on the paragraph). outlineLvl=1
        // (0-based) => H2, proving the level is read from the style, not hardcoded to 1.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "Section",
            "<w:style w:type=\"paragraph\" w:styleId=\"Section\"><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBe(2);
        }
    }

    [Fact]
    public void Resolves_a_custom_style_through_a_multi_hop_basedOn_chain()
    {
        // #316: A -> B -> Heading2. The walk must follow more than one basedOn hop.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "A",
            "<w:style w:type=\"paragraph\" w:styleId=\"A\"><w:basedOn w:val=\"B\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"B\"><w:basedOn w:val=\"Heading2\"/></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBe(2);
        }
    }

    [Fact]
    public void A_custom_style_that_is_not_a_heading_returns_null()
    {
        // A custom style based on Normal with no outline level is body text, not a heading.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "BodyCustom",
            "<w:style w:type=\"paragraph\" w:styleId=\"BodyCustom\"><w:basedOn w:val=\"Normal\"/></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBeNull();
        }
    }

    [Fact]
    public void A_custom_heading_style_without_a_styles_part_is_not_resolved()
    {
        // Backward-compatible fallback: with no MainDocumentPart, a custom style id cannot be resolved against
        // styles.xml, so it is not treated as a heading (the pre-#316 single-arg behavior is unchanged).
        WordStyleMap.HeadingLevel(WithStyle("ChapterTitle")).ShouldBeNull();
    }

    [Fact]
    public void An_explicit_paragraph_outline_of_nine_cancels_a_heading_style()
    {
        // #316: the paragraph's custom style is based on Heading1, but the paragraph carries a direct
        // outlineLvl=9 ("Body Text" chosen in the paragraph dialog). Direct formatting overrides the style, so
        // it is body text — not H1. Without the explicit-9 short-circuit this fell through to the style chain
        // and wrongly rendered a heading.
        var (document, mainPart, _) = BuildWithStyles(
            "ChapterTitle",
            "<w:style w:type=\"paragraph\" w:styleId=\"ChapterTitle\"><w:basedOn w:val=\"Heading1\"/></w:style>");
        using (document)
        {
            var paragraph = new W.Paragraph(new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "ChapterTitle" },
                new W.OutlineLevel { Val = 9 }));
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBeNull();
        }
    }

    [Fact]
    public void A_style_level_outline_of_nine_overrides_a_based_on_heading()
    {
        // #316: the custom style is based on Heading1 but explicitly sets outlineLvl=9 (body text) in its own
        // definition — the closest override wins, so it is not a heading. Without the explicit-9 stop the walk
        // continued up to Heading1 and returned H1.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "QuietHeading",
            "<w:style w:type=\"paragraph\" w:styleId=\"QuietHeading\"><w:basedOn w:val=\"Heading1\"/>"
            + "<w:pPr><w:outlineLvl w:val=\"9\"/></w:pPr></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBeNull();
        }
    }

    [Fact]
    public void A_cyclic_basedOn_chain_terminates_and_is_not_a_heading()
    {
        // Defense: a malformed A -> B -> A cycle must not loop forever (MaxStyleChainDepth bounds the walk)
        // and, resolving to no built-in heading / outline, is body text.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "A",
            "<w:style w:type=\"paragraph\" w:styleId=\"A\"><w:basedOn w:val=\"B\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"B\"><w:basedOn w:val=\"A\"/></w:style>");
        using (document)
        {
            WordStyleMap.HeadingLevel(paragraph, mainPart).ShouldBeNull();
        }
    }

    [Fact]
    public void Caches_a_resolved_custom_style_heading_level()
    {
        // #458: the first resolution of a custom style writes styleId -> level into the memo, so subsequent
        // paragraphs sharing the style skip the basedOn-chain walk over styles.xml.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "ChapterTitle",
            "<w:style w:type=\"paragraph\" w:styleId=\"ChapterTitle\"><w:basedOn w:val=\"Heading1\"/></w:style>");
        using (document)
        {
            var cache = new Dictionary<string, int?>();
            WordStyleMap.HeadingLevel(paragraph, mainPart, cache).ShouldBe(1);
            cache.ShouldContainKeyAndValue("ChapterTitle", 1);
        }
    }

    [Fact]
    public void A_cache_hit_short_circuits_the_style_chain_walk()
    {
        // #458: a hit on the memo returns the cached level without touching styles.xml. Pre-seed a sentinel the
        // chain would never produce (the style is based on Heading1 => H1) and assert the sentinel is returned,
        // proving the memo — not the walk — supplied the answer.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "ChapterTitle",
            "<w:style w:type=\"paragraph\" w:styleId=\"ChapterTitle\"><w:basedOn w:val=\"Heading1\"/></w:style>");
        using (document)
        {
            var cache = new Dictionary<string, int?> { ["ChapterTitle"] = 4 };
            WordStyleMap.HeadingLevel(paragraph, mainPart, cache).ShouldBe(4);
        }
    }

    [Fact]
    public void Caches_a_non_heading_custom_style_as_a_null_level()
    {
        // #458: "resolved, not a heading" is a real cached result. The memo stores the key with a null value so
        // a repeated body-text paragraph is a cache HIT (absence-of-key is the miss, not null-value) and does
        // not re-walk the chain.
        var (document, mainPart, paragraph) = BuildWithStyles(
            "BodyCustom",
            "<w:style w:type=\"paragraph\" w:styleId=\"BodyCustom\"><w:basedOn w:val=\"Normal\"/></w:style>");
        using (document)
        {
            var cache = new Dictionary<string, int?>();
            WordStyleMap.HeadingLevel(paragraph, mainPart, cache).ShouldBeNull();
            cache.ShouldContainKey("BodyCustom");
            cache["BodyCustom"].ShouldBeNull();
        }
    }
}
