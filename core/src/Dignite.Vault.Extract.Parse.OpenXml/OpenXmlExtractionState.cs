using System.Collections.Generic;
using Dignite.Vault.Extract.Abstractions.Parse;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Mutable per-extraction accumulator shared by <see cref="DocxExtractor"/> and <see cref="PptxExtractor"/>:
/// the remaining per-file image budget, the resolved OCR language hints, and the #268 loss counters that
/// <see cref="OpenXmlIncompleteReason"/> turns into the completeness signal. Extracted from the two formerly
/// duplicated (and inconsistently <c>protected</c>/<c>private</c>) nested <c>ExtractionState</c> classes
/// (#317) so a new counter is declared in exactly one place. The "failed container" counter is
/// format-neutral (<see cref="FailedContainers"/> — a Word document block or a PowerPoint slide); the format
/// supplies the noun when building the reason. PPTX adds a reading-order <c>Sequence</c> in its own
/// <c>PptxExtractionState</c> subclass; DOCX emits in flow (document) order and needs no such field.
/// </summary>
public class OpenXmlExtractionState
{
    /// <summary>Remaining per-file image-transcription budget (decremented once per real figure sent to OCR).</summary>
    public int ImageBudget;

    /// <summary>Top-level containers (DOCX body blocks / PPTX slides) that faulted on parse and were skipped.</summary>
    public int FailedContainers;

    /// <summary>Images skipped after the per-file image cap was reached.</summary>
    public int DroppedByCap;

    /// <summary>Images that could not be decoded to a supported raster format (vector / corrupt / mislabeled).</summary>
    public int Undecodable;

    /// <summary>Images that exceeded the per-image byte cap and were skipped before materialization.</summary>
    public int OversizedImages;

    /// <summary>Image transcriptions truncated at the token limit or discarded by the OCR repetition guard.</summary>
    public int TruncatedOcr;

    /// <summary>Images whose OCR failed with a provider error (the figure was skipped, the text kept).</summary>
    public int FailedFigureOcr;

    /// <summary>Charts whose backing data could not be rendered as a Markdown table.</summary>
    public int ChartFailures;

    /// <summary>OCR language hints for embedded-image transcription (resolved once per extraction).</summary>
    public IList<string> LanguageHints = new List<string>();

    /// <summary>
    /// Resolves the OCR language hints for embedded-image transcription: the per-document hints from the
    /// context, or empty. There is no central host default (#441 removed it); a provider that needs a
    /// language default reads its own config (e.g. <c>PaddleOcr:Languages</c>). Shared by both OpenXML
    /// extractors' <c>protected virtual ResolveLanguageHints</c> override seam (#317) so the resolution rule
    /// lives in one place while each extractor keeps its overridable entry point.
    /// </summary>
    public static IList<string> ResolveLanguageHints(TextExtractionContext context)
        => context.LanguageHints ?? new List<string>();
}
