using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse.Pdf;

/// <summary>
/// Tests the #268 incompleteness-reason builder directly: it is the single source of truth for both the
/// reason text and completeness (<c>IsComplete = reason is null</c>), and it must not conflate the
/// "page text lost" and "only images skipped" failure modes.
/// </summary>
public class PdfIncompleteReason_Tests
{
    // (failedPages, pagesWithSkippedImages, droppedByCap, undecodable, truncatedOcr, failedFigureOcr)

    [Fact]
    public void Returns_null_when_nothing_was_lost()
    {
        // null reason ⇒ IsComplete = true. This is what makes the boolean derive from one source.
        PdfExtractor.BuildIncompleteReason(0, 0, 0, 0, 0, 0).ShouldBeNull();
    }

    [Fact]
    public void Reports_a_dropped_page_as_unparsed()
    {
        var reason = PdfExtractor.BuildIncompleteReason(
            failedPages: 1, pagesWithSkippedImages: 0, droppedByCap: 0, undecodable: 0, truncatedOcr: 0, failedFigureOcr: 0);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("could not be parsed");
    }

    [Fact]
    public void Reports_images_only_failure_without_claiming_page_text_was_lost()
    {
        // GetImages faulted but the page's text was retained — must NOT say the page was skipped/unparsed.
        var reason = PdfExtractor.BuildIncompleteReason(
            failedPages: 0, pagesWithSkippedImages: 1, droppedByCap: 0, undecodable: 0, truncatedOcr: 0, failedFigureOcr: 0);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("page text retained");
        reason.ShouldNotContain("could not be parsed");
    }

    [Fact]
    public void Reports_a_failed_figure_ocr()
    {
        var reason = PdfExtractor.BuildIncompleteReason(
            failedPages: 0, pagesWithSkippedImages: 0, droppedByCap: 0, undecodable: 0, truncatedOcr: 0, failedFigureOcr: 2);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("failed OCR");
    }
}
