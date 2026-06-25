using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Unit tests for the single source of truth of the #268 completeness signal: the reason string is null
/// iff nothing was lost, and lists every loss cause that occurred. Guards against a new counter drifting
/// out of sync with a separate boolean predicate.
/// </summary>
public class PptxIncompleteReason_Tests
{
    [Fact]
    public void Null_when_nothing_was_lost()
    {
        PptxExtractor.BuildIncompleteReason(0, 0, 0, 0, 0, 0, 0).ShouldBeNull();
    }

    [Fact]
    public void Describes_each_loss_cause_that_occurred()
    {
        var reason = PptxExtractor.BuildIncompleteReason(
            failedSlides: 1, droppedByCap: 2, undecodable: 3, oversizedImages: 7,
            truncatedOcr: 4, failedFigureOcr: 5, chartFailures: 6);

        reason.ShouldNotBeNull();
        reason!.ShouldContain("1 slide");
        reason.ShouldContain("3 embedded image");   // undecodable
        reason.ShouldContain("7 embedded image");   // oversized
        reason.ShouldContain("size cap");
        reason.ShouldContain("5 embedded image");   // failed OCR
        reason.ShouldContain("4 image transcription");
        reason.ShouldContain("6 chart");
        reason.ShouldContain("2 image");            // cap
        reason.ShouldEndWith(".");
    }

    [Fact]
    public void Single_cause_reads_cleanly()
    {
        PptxExtractor.BuildIncompleteReason(0, 0, 1, 0, 0, 0, 0)
            .ShouldBe("1 embedded image(s) could not be decoded to a supported raster format (e.g. EMF/WMF vector).");
    }
}
