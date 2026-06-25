namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Tuning for the OpenXML providers (PPTX now, DOCX in #308). Each embedded image becomes one
/// <c>IOcrProvider.RecognizeAsync</c> call (potentially one paid vision-LLM call), so the image knobs
/// guard cost and noise. The image-count / pixel knobs mirror <c>PdfExtractorOptions</c>; the byte cap
/// (<see cref="MaxImageBytesPerImage"/>) is OpenXML-specific (ZIP-container decompression-bomb guard) and
/// has no PDF counterpart. A host can override via <c>Configure&lt;OpenXmlExtractorOptions&gt;</c> / the
/// <c>OpenXmlExtractor</c> configuration section (see the host appsettings for an example).
/// </summary>
public class OpenXmlExtractorOptions
{
    /// <summary>
    /// Hard cap on how many embedded images are transcribed per file. Images beyond the cap are skipped
    /// and the result is marked incomplete (#268) — the document text is still returned, so an image-heavy
    /// file degrades gracefully instead of failing or running away on OCR cost. Named per-file (not
    /// per-presentation) because this options class is shared with the DOCX phase (#308).
    /// </summary>
    public int MaxImagesPerFile { get; set; } = 50;

    /// <summary>
    /// Minimum pixel count (<c>width * height</c>) for an embedded image to be transcribed. Smaller
    /// images (icons, bullets, logos, spacers) are decorative, not figure content, and are skipped
    /// silently (not counted against completeness). Default ≈ 64×64.
    /// <para>
    /// <b>Unit caveat:</b> for OpenXML the dimensions are the shape's <b>display</b> extents (EMU → px at
    /// 96 DPI), <i>not</i> the raster's native sample count (PPTX does not expose that without decoding the
    /// bytes). So a high-resolution photo shrunk to a small thumbnail on a slide is judged by its displayed
    /// size, not its source resolution — set this conservatively if decks scale figures down heavily. (The
    /// PDF provider's same-named option is closer to native samples; keep that difference in mind when
    /// tuning across formats.)
    /// </para>
    /// </summary>
    public int MinImagePixels { get; set; } = 64 * 64;

    /// <summary>
    /// Hard cap on the decompressed byte size of a single embedded image. An image part exceeding this is
    /// skipped (never fully buffered into managed memory) and the result is marked incomplete (#268).
    /// <para>
    /// This guards a real amplification vector that <see cref="MaxImagesPerFile"/> does not: a PPTX/DOCX is
    /// a ZIP container, so a small file can carry a highly-compressed image part that inflates to gigabytes
    /// when read — without a byte cap a single such part would OOM / thrash the extraction worker before any
    /// recoverable signal. Default 16 MiB (a conservative ceiling for a single embedded figure).
    /// </para>
    /// </summary>
    public long MaxImageBytesPerImage { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Whether to append each slide's speaker notes to the Markdown (under a per-slide "Speaker notes"
    /// heading). <b>Default off.</b> Speaker notes are author-private content that is <b>not visible to the
    /// audience</b>; since Markdown is the channel's sole egress payload (REST / MCP / EventBus / Webhook),
    /// including notes by default would silently leak internal annotations into every downstream consumer.
    /// A host/admin that wants notes indexed must <b>explicitly opt in</b> by setting this to <c>true</c>.
    /// </summary>
    public bool IncludeSpeakerNotes { get; set; } = false;
}
