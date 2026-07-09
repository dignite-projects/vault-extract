namespace Dignite.Vault.Extract.Abstractions.Parse;

public class TextExtractionResult
{
    /// <summary>
    /// Structured Markdown output. Empty string when the provider recognizes no content.
    /// This is the <b>only</b> text payload of the Parse pipeline; downstream consumers that
    /// need plain text should project it through <see cref="Dignite.Vault.Extract.Documents.MarkdownStripper"/>.
    /// </summary>
    public string Markdown { get; set; } = string.Empty;

    public string? DetectedLanguage { get; set; }

    /// <summary>true = OCR (physical scan), false = direct text layer (digital)</summary>
    public bool UsedOcr { get; set; }

    /// <summary>Winning provider family / name, meaning the provider that ultimately produced Markdown; nullable for historical / unknown cases.</summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Whether this text extraction is <b>complete</b> (#268). <c>true</c> (default) means all content
    /// was captured; <c>false</c> means content is known to be missing, such as OCR output truncated by
    /// token limits, duplicate-guard drops, or pages in a multi-page PDF that could not be transcribed.
    /// When providers do not set this signal, the default is complete and behavior is unchanged.
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>Short diagnostic when incomplete (<see cref="IsComplete"/> is false); <c>null</c> when complete.</summary>
    public string? IncompleteReason { get; set; }

    /// <summary>
    /// <b>Native output payload</b> from the winning provider (raw spatial-signal material, #210);
    /// <c>null</c> when absent. Archived to blob by the text extraction job: it <b>does not enter the
    /// DB</b> and is <b>not exposed as a parallel text field</b>.
    /// </summary>
    public NativePayload? NativePayload { get; set; }

    /// <summary>
    /// Number of embedded-image OCR calls <b>dispatched</b> via figure-OCR (#306) — every call sent to
    /// <c>IOcrProvider</c> for an embedded figure, <b>including ones that threw</b> (a failed call may still
    /// incur provider cost / tokens), so this counts dispatched attempts, not successful transcriptions. A
    /// digital document reports <see cref="UsedOcr"/> = false (it is a digital extraction) yet may dispatch
    /// embedded-figure OCR; this named counter lets downstream audit / cost-attribution see that embedded-image
    /// OCR occurred without overloading the binary <see cref="UsedOcr"/> "scan vs digital" flag. 0 when no
    /// figure OCR ran.
    /// </summary>
    public int FigureOcrCount { get; set; }
}
