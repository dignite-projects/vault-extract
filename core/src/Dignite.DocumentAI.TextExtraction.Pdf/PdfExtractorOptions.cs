namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Tuning for <see cref="PdfExtractor"/>. Each embedded image becomes one
/// <c>IOcrProvider.RecognizeAsync</c> call (potentially one paid vision-LLM call), so the knobs guard
/// cost and noise. Defaults are conservative; a host can override via <c>Configure&lt;PdfExtractorOptions&gt;</c>.
/// </summary>
public class PdfExtractorOptions
{
    /// <summary>
    /// Hard cap on how many embedded images are transcribed per document. Images beyond the cap are
    /// skipped and the result is marked incomplete (#268) — the digital text layer is still returned, so
    /// an image-heavy PDF degrades gracefully instead of failing or running away on OCR cost.
    /// </summary>
    public int MaxImagesPerPdf { get; set; } = 50;

    /// <summary>
    /// Minimum pixel count (<c>WidthInSamples * HeightInSamples</c>) for an embedded image to be
    /// transcribed. Smaller images (icons, bullets, rules, 1px spacers) are decorative, not figure
    /// content, and are skipped silently (not counted against completeness). Default ≈ 64×64.
    /// </summary>
    public int MinImagePixels { get; set; } = 64 * 64;

    /// <summary>
    /// Whether to skip the full-page scan background of a searchable / "sandwich" PDF (#309). Such PDFs
    /// store, per page, the full-page scan raster plus an (often invisible) OCR text layer over it; without
    /// this guard the raster is re-OCR'd as a figure, duplicating the already-extracted text and burning a
    /// redundant (paid) vision call per page. The guard is high-precision and errs toward keeping — see
    /// <see cref="PdfExtractor.IsFullPageScanBackground"/> — so a real full-page figure is never dropped.
    /// Set <c>false</c> to restore the unconditional #301 behavior (every embedded image is transcribed).
    /// </summary>
    public bool SkipFullPageScanBackground { get; set; } = true;

    /// <summary>
    /// Skip signal 1 (necessary): the minimum fraction of the page box (CropBox, falling back to MediaBox)
    /// that the image's PLACEMENT bbox must cover, on BOTH width and height, to be considered a full-page
    /// background. Compared against placement geometry, not pixel dimensions
    /// (<c>WidthInSamples × HeightInSamples</c>) — a low-DPI scan can be page-sized on the page yet small in
    /// samples. Only consulted when <see cref="SkipFullPageScanBackground"/> is enabled.
    /// </summary>
    public double FullPageScanCoverageThreshold { get; set; } = 0.9;

    /// <summary>
    /// Skip signal 2 (necessary): the minimum number of reconstructed text lines over the image region for a
    /// VISIBLE text layer to read as a whole-page transcription rather than a label/caption. Deliberately
    /// high: the visible-line path is the weakest skip signal — it has no invisible-layer corroboration, and
    /// a wrong skip is silent (it does not trip the #268 completeness signal) — so the bar errs strongly
    /// toward keeping a real figure. A real full-page figure essentially never carries this many lines of
    /// selectable digital body text spanning the page; a genuine scan transcription carries far more. A
    /// predominantly invisible (Tr 3) layer is the canonical sandwich signature and bypasses this count via
    /// <see cref="FullPageScanMinInvisibleTextRatio"/>.
    /// </summary>
    public int FullPageScanMinTextLines { get; set; } = 15;

    /// <summary>
    /// Skip signal 2 (necessary): the minimum fraction of the image's height that the text block must
    /// vertically span. This is the PRIMARY guard against dropping a full-page figure whose only text is a
    /// thin caption band at an edge (one line / a small cluster spans little of the height); a whole-page
    /// transcription spans most of it.
    /// </summary>
    public double FullPageScanMinTextVerticalCoverage { get; set; } = 0.5;

    /// <summary>
    /// Skip signal 2 — Tr 3 bonus (sufficient, not necessary): the minimum fraction of in-region letters
    /// drawn in the invisible text-rendering mode (<c>Tr 3</c>) for the text layer to count as a scan
    /// sandwich even when it has fewer than <see cref="FullPageScanMinTextLines"/> lines. An OCR layer is
    /// invisible by construction, so a predominantly invisible full-page text layer is the canonical
    /// sandwich signature; a real figure never carries one. The vertical-span guard still applies.
    /// </summary>
    public double FullPageScanMinInvisibleTextRatio { get; set; } = 0.6;

    /// <summary>
    /// Whether to reconstruct a detected table region of the digital text layer into a Markdown table
    /// (#310 Phase B). When a region confidently forms a 2-D grid (>= 2x2, aligned columns, sufficient
    /// fill) its rows render as Markdown table rows, so a wrapped cell stays in its own cell instead of
    /// interleaving with its row siblings (the #310 料金表 failure). Detection is <b>non-lossy</b>: a region
    /// that fails the table test degrades to the Phase A column-aware paragraph linearization
    /// (<see cref="PdfReadingOrder.RenderPage"/>) — never forcing a bad table, never dropping a cell. This is
    /// a pure layout/rendering heuristic with no OCR cost; set <c>false</c> to keep the Phase A paragraph
    /// rendering for every region. The grid-geometry thresholds themselves are internal constants of
    /// <see cref="PdfTableReconstruction"/> (like the caption-distance / paragraph-pitch constants of
    /// <see cref="PdfReadingOrder"/>), not options — only this on/off switch is host-configurable.
    /// </summary>
    public bool ReconstructTables { get; set; } = true;
}
