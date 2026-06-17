using System.Collections.Generic;

namespace Dignite.DocumentAI.Abstractions.TextExtraction;

public class TextExtractionResult
{
    /// <summary>
    /// Structured Markdown output. Empty string when the provider recognizes no content.
    /// This is the <b>only</b> text payload of the TextExtraction pipeline; downstream consumers that
    /// need plain text should project it through <see cref="Dignite.DocumentAI.Documents.MarkdownStripper"/>.
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
    /// Out-of-band embedded-figure signal (#306): named / strongly-typed / nullable (the #210
    /// <c>PageBlocks</c> precedent, <b>not</b> a <c>Dictionary&lt;string,object&gt;</c> bag, #206). Carries
    /// each transcribed embedded image's bytes + provenance so the channel can persist candidate crops and
    /// route a figure that is itself a document to its own derived <c>Document</c> (Scenario B).
    /// <b>Orthogonal to inline-into-Markdown (#301)</b>: inlining stays the text payload; this is the
    /// separate out-of-band channel. <c>null</c> when the provider surfaces no figures.
    /// </summary>
    public IReadOnlyList<Figure>? Figures { get; set; }

    /// <summary>
    /// Number of embedded images transcribed via figure-OCR (#306). A digital document reports
    /// <see cref="UsedOcr"/> = false (it is a digital extraction) yet may transcribe embedded figures
    /// through <c>IOcrProvider</c>; this named counter lets downstream audit / cost-attribution see that
    /// embedded-image OCR occurred without overloading the binary <see cref="UsedOcr"/> "scan vs digital"
    /// flag. 0 when no figure OCR ran.
    /// </summary>
    public int FigureOcrCount { get; set; }
}
