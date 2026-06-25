using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Extract.Abstractions.Parse;

/// <summary>
/// Text extraction capability port. It is a pure capability: receives a file stream and context,
/// returns an extraction result, knows nothing about the Document aggregate, and does not access
/// repositories.
/// Implementation: Dignite.Vault.Extract.Parse.
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first contract</b>: implementations <b>must</b> return Markdown text in
/// <see cref="TextExtractionResult.Markdown"/>. Markdown is consumed by vectorization
/// (structure-aware chunking), LLM classification / QA / rerank, and business-module field
/// extraction.
/// </para>
/// <para>
/// <b>For structured documents</b> (contracts / reports / CSV / titled DOCX / layout-aware OCR
/// output), headings, tables, and lists are real LLM reasoning signals.
/// <b>For unstructured content</b> (loose OCR paragraphs / plain txt / PP-OCRv4 line output), flat
/// Markdown paragraphs are literally equivalent to plain text reassembled with double newlines.
/// Markdown is a <b>container name</b>, not a signal gain, and this path exists so downstream
/// consumers always consume one format.
/// </para>
/// <para>
/// Even when the source file has no structure, output flat Markdown paragraphs instead of falling
/// back to a separate "plain text" path or adding a parallel plain-text field to
/// <see cref="TextExtractionResult"/>. Downstream consumers that need plain text should project it on
/// the consuming side through <c>Dignite.Vault.Extract.Documents.MarkdownStripper</c>.
/// </para>
/// <para>
/// <b>Out-of-band signals</b> (coordinates / page metadata / form key-value pairs) are orthogonal to
/// Markdown. Future extensions should add named optional strongly typed fields on
/// <see cref="TextExtractionResult"/> instead of stuffing them back into the Markdown string or a
/// generic extension slot.
/// </para>
/// </remarks>
public interface ITextExtractor
{
    /// <summary>
    /// Extracts Markdown from a file stream.
    /// </summary>
    /// <param name="fileStream">The original file stream.</param>
    /// <param name="context">Business-agnostic extraction context (contentType / file name / expected language, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Extraction result containing <see cref="TextExtractionResult.Markdown"/>.
    /// When no content is recognized, <see cref="TextExtractionResult.Markdown"/> is an empty string;
    /// implementations <b>must not</b> return <c>null</c> or throw an exception to represent "no
    /// content".
    /// </returns>
    Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default);
}
