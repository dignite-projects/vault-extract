using System.Collections.Generic;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Builds the #268 extraction-incompleteness reason shared by <see cref="DocxExtractor"/> and
/// <see cref="PptxExtractor"/> from their loss counters, or returns <c>null</c> when nothing was lost.
/// Single source of truth for both the reason text and completeness (<c>IsComplete = reason is null</c>),
/// so a new counter cannot drift out of sync with a separate boolean predicate — and, being shared, a change
/// to the wording / branch set / ordering now applies to <b>both</b> OpenXML extractors at once (#317,
/// extracted from the two formerly character-for-character-identical <c>BuildIncompleteReason</c> bodies).
/// Only the first cause differs per format — a Word <c>document block</c> vs a PowerPoint <c>slide</c> —
/// supplied by the caller as <paramref name="failedContainerNoun"/>.
/// </summary>
internal static class OpenXmlIncompleteReason
{
    public static string? Build(
        string failedContainerNoun,
        int failedContainers,
        int droppedByCap,
        int undecodable,
        int oversizedImages,
        int truncatedOcr,
        int failedFigureOcr,
        int chartFailures)
    {
        var parts = new List<string>();
        if (failedContainers > 0)
        {
            parts.Add($"{failedContainers} {failedContainerNoun}(s) could not be parsed and were skipped");
        }

        if (undecodable > 0)
        {
            parts.Add($"{undecodable} embedded image(s) could not be decoded to a supported raster format (e.g. EMF/WMF vector)");
        }

        if (oversizedImages > 0)
        {
            parts.Add($"{oversizedImages} embedded image(s) exceeded the per-image size cap and were skipped");
        }

        if (failedFigureOcr > 0)
        {
            parts.Add($"{failedFigureOcr} embedded image(s) failed OCR (provider error)");
        }

        if (truncatedOcr > 0)
        {
            parts.Add($"{truncatedOcr} image transcription(s) were truncated or discarded by the OCR provider");
        }

        if (chartFailures > 0)
        {
            parts.Add($"{chartFailures} chart(s) could not be rendered as a table");
        }

        if (droppedByCap > 0)
        {
            parts.Add($"{droppedByCap} image(s) were skipped after reaching the per-file image cap");
        }

        return parts.Count == 0 ? null : string.Join("; ", parts) + ".";
    }
}
