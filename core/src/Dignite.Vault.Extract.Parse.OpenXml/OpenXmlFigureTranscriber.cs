using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;
using Dignite.Vault.Extract.Ocr;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;

namespace Dignite.Vault.Extract.Parse.OpenXml;

/// <summary>
/// Resolves an embedded OpenXML image relationship to bytes, transcribes it through the host-selected
/// <see cref="IOcrProvider"/>, and returns the (optionally captioned) Markdown block — or <c>null</c> when
/// there is nothing to emit. This is the figure-OCR pipeline formerly copy-pasted between
/// <c>DocxExtractor.TranscribeEmbeddedImageAsync</c> and <c>PptxExtractor.HandlePictureAsync</c>: the budget
/// guard, <see cref="OpenXmlImagePayload.TryResolve"/> call, the image-outcome switch, the OCR call, the
/// truncation signal, and the caption formatting were character-for-character identical, so a future change
/// (a new <c>ImageOutcome</c>, a new #268 counter, a cap-ordering or security tightening) had to be applied
/// in two places and compiled cleanly if one was missed (#317).
/// <para>
/// It honors the per-file image budget and trips the #268 loss counters on
/// cap / oversize / undecodable / OCR-failure / truncation, mutating them on the passed
/// <see cref="OpenXmlExtractionState"/>. The caller owns everything format-specific: the pre-checks
/// (decorative-size filtering, blip/embed presence), the caption source (Word <c>wp:docPr</c> vs PPTX
/// <c>p:cNvPr</c>), the part container to resolve the relationship against (<c>MainDocumentPart</c> vs
/// <c>SlidePart</c>), and how the returned block is sunk (DOCX appends the string in flow order; PPTX wraps
/// it in a positioned <c>SlideBlock</c> for reading-order sorting).
/// </para>
/// <para>
/// <b>Transcription only</b> — the figure's bytes are the OCR input, so no user free-text enters a prompt
/// (no <c>PromptBoundary</c> concern), and <c>UsedOcr</c> ("scan vs digital") stays the caller's <c>false</c>
/// because figure OCR is auxiliary to a digital extraction (same contract reasoning as PdfExtractor #301).
/// </para>
/// </summary>
internal static class OpenXmlFigureTranscriber
{
    public static async Task<string?> TranscribeAsync(
        OpenXmlPartContainer partContainer,
        string relationshipId,
        string? caption,
        int? pageNumber,
        OpenXmlExtractionState state,
        OpenXmlExtractorOptions options,
        IOcrProvider ocrProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (state.ImageBudget <= 0)
        {
            state.DroppedByCap++;
            return null;
        }

        OpenXmlImagePayload.ResolvedImage resolved;
        try
        {
            var part = partContainer.GetPartById(relationshipId);
            resolved = part is ImagePart imagePart
                ? OpenXmlImagePayload.TryResolve(imagePart, options.MaxImageBytesPerImage)
                // A dangling relationship / non-image part: treat as undecodable.
                : new OpenXmlImagePayload.ResolvedImage(OpenXmlImagePayload.ImageOutcome.Undecodable, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve/decode an embedded OpenXML image; skipping it.");
            resolved = new OpenXmlImagePayload.ResolvedImage(OpenXmlImagePayload.ImageOutcome.Undecodable, null, null);
        }

        switch (resolved.Outcome)
        {
            case OpenXmlImagePayload.ImageOutcome.Oversized:
                // A single image larger than the per-image byte cap (e.g. a ZIP-decompression bomb). Skipped
                // before full materialization; trips the completeness signal but never OOMs the worker.
                logger.LogWarning("Skipped an embedded image exceeding the {Cap}-byte per-image cap.", options.MaxImageBytesPerImage);
                state.OversizedImages++;
                return null;

            case OpenXmlImagePayload.ImageOutcome.Undecodable:
                // Vector (EMF/WMF), dangling relationship, or undecodable/mislabeled bytes.
                state.Undecodable++;
                return null;

            case OpenXmlImagePayload.ImageOutcome.Ok:
                break;

            default:
                // A future outcome added to the enum must not silently fall through to RecognizeAsync with
                // possibly-null bytes — fail closed by treating it as undecodable.
                logger.LogWarning("Unhandled image outcome {Outcome}; treating as undecodable.", resolved.Outcome);
                state.Undecodable++;
                return null;
        }

        state.ImageBudget--;

        OcrResult ocr;
        try
        {
            using var imageStream = new MemoryStream(resolved.Bytes!, writable: false);
            ocr = await ocrProvider.RecognizeAsync(
                imageStream,
                new OcrOptions
                {
                    ContentType = resolved.ContentType!,
                    LanguageHints = state.LanguageHints
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A single figure's OCR failing (provider timeout / rate-limit / auth / one bad image) must NOT
            // discard the text already extracted — figure OCR is an auxiliary augmentation, not the primary
            // payload (the #210/#268 "auxiliary step must not break the main pipeline" principle). Skip this
            // figure and trip the signal. OperationCanceledException still propagates so a host/job shutdown
            // aborts promptly.
            logger.LogWarning(ex, "Embedded-image OCR failed; keeping the extracted text, skipping this figure.");
            state.FailedFigureOcr++;
            return null;
        }

        if (!ocr.IsComplete)
        {
            // OCR truncated at the token limit or discarded by the repetition guard.
            state.TruncatedOcr++;
        }

        var transcription = ocr.Markdown?.Trim() ?? string.Empty;
        if (transcription.Length == 0)
        {
            return null;
        }

        // #480: emit the byte-identical figure block the PDF path emits (PdfReadingOrder) — the transcription wrapped
        // in the *[Image OCR]*…*[End OCR]* provenance markers (#371/#381), with the native caption escaped as a block
        // BEFORE the open marker (not bolded, not inside the span) so the span body stays transcription-only and the
        // #373 child seed (ExtractBodies) stays clean. Before #480 OpenXML figures carried no markers, so
        // ImageOcrMarkup.Contains was blind to them — no deterministic segmentation routing (#379). Caption
        // (alt-text) is author-controlled source text, so EscapeBlockText neutralizes any leading block marker /
        // inline link it carries (multi-line safe, unlike the old bold label).
        var figureMarkup = ImageOcrMarkup.Wrap(transcription, pageNumber);
        return string.IsNullOrWhiteSpace(caption)
            ? figureMarkup
            : MarkdownText.EscapeBlockText(caption) + "\n\n" + figureMarkup;
    }
}
