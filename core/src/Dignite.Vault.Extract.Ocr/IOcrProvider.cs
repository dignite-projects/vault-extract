using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Extract.Ocr;

/// <summary>
/// OCR service provider interface: the minimal contract layer for OCR provider implementations.
/// Third-party integrations for the Extract text extraction pipeline only need to reference
/// <c>Dignite.Vault.Extract.Ocr</c> to obtain <see cref="IOcrProvider"/> + <see cref="OcrOptions"/> +
/// <see cref="OcrResult"/>. They do not need to reference <c>Dignite.Vault.Extract.Parse</c>
/// (the orchestrator) or <c>Dignite.Vault.Extract.Abstractions</c> (the top-level ITextExtractor
/// contract).
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first contract</b>: implementations <b>must</b> populate
/// <see cref="OcrResult.Markdown"/>. If the underlying service output is already layout-aware
/// Markdown, such as PaddleOCR PP-StructureV3 or Azure DI <c>prebuilt-layout</c>, pass it through
/// directly; in that case headings, tables, and lists are real LLM-understanding signals. If the
/// underlying service returns only plain text, such as PaddleOCR PP-OCRv4, the provider itself must
/// wrap paragraphs as flat Markdown, for example <c>string.Join("\n\n", paragraphs)</c>. Do not leave
/// that translation responsibility to the upstream orchestrator.
/// </para>
/// <para>
/// This flat wrapping exists <b>so downstream consumers handle one format</b>. For unstructured OCR
/// output it <b>does not add signal gain</b>. Do not describe it as "flat paragraphs are also Markdown
/// signal"; the honest view is that Markdown is the unified container name for the text payload, and
/// structured content is what makes markup actually carry meaning.
/// </para>
/// <para>
/// <b>Out-of-band signals</b> (per-page coordinates / bbox / stamp locations / form key-value pairs)
/// are orthogonal to this contract's Markdown field. <see cref="OcrResult"/> deliberately exposes
/// only text-related fields today. If requirements such as page-aware citations land later, add named
/// optional strongly typed fields to <see cref="OcrResult"/>. Generic Dictionary extension slots are
/// forbidden.
/// </para>
/// </remarks>
public interface IOcrProvider
{
    /// <param name="cancellationToken">
    /// Passed through from the text extraction background job via the ABP background job cancellation
    /// token (host shutdown / job cancellation). OCR calls are usually long-running external HTTP
    /// work, such as a local sidecar or cloud LRO polling, so implementations <b>must</b> forward this
    /// token to the underlying HTTP / SDK calls so job or host shutdown can abort promptly.
    /// </param>
    Task<OcrResult> RecognizeAsync(Stream fileStream, OcrOptions options, CancellationToken cancellationToken = default);
}
