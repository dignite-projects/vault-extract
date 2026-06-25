using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Parse;

namespace Dignite.Vault.Extract.Parse;

/// <summary>
/// Provider abstraction for digital documents (PDF / Word / HTML / plain text / CSV / RTF / EPUB,
/// etc.) to Markdown. Handles files with a digital text layer and complements <c>IOcrProvider</c>,
/// which handles images / scans.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coexisting, dispatched by file extension</b> (not isomorphic to <c>IOcrProvider</c>, which is
/// host-selected and mutually exclusive — exactly one enabled via <c>DependsOn</c>). Multiple
/// <see cref="IMarkdownTextProvider"/> implementations are registered side by side; for each file
/// <c>DefaultTextExtractor</c> selects the highest-<see cref="Priority"/> provider whose
/// <see cref="CanHandle"/> returns <c>true</c>. Each provider self-declares the extensions it owns
/// (e.g. a PdfPig-based provider claims <c>.pdf</c>), and
/// <c>Dignite.Vault.Extract.Parse.ElBrunoMarkItDown</c> is the catch-all fallback
/// (<see cref="CanHandle"/> always <c>true</c>, lowest <see cref="Priority"/>). Not installing a
/// specialized provider module therefore makes that extension gracefully fall back to the catch-all,
/// preserving prior behavior.
/// </para>
/// <para>
/// <b>Markdown-first contract</b>: implementations <b>must</b> populate extraction output into
/// <see cref="TextExtractionResult.Markdown"/> and <b>must not</b> fall back to plain text or add a
/// parallel plain-text field. Any "plain text fallback" is a design violation.
/// </para>
/// <para>
/// <b>For structured documents</b> (titled DOCX / well-laid-out PDF / CSV table), Markdown headings,
/// tables, and lists are real signals for downstream vectorization chunking (structure-aware) and LLM
/// understanding, so use them fully. <b>For unstructured content</b> (bare txt / single-paragraph
/// RTF), Markdown is a <b>container name</b>, not a signal gain; the contract is kept only so
/// downstream consumers handle one format.
/// </para>
/// </remarks>
public interface IMarkdownTextProvider
{
    /// <summary>
    /// Whether this provider can extract the given file extension. The extension includes the leading
    /// dot (e.g. <c>".pdf"</c>) and may be empty/null. Specialized providers return <c>true</c> only
    /// for the extensions they own; the catch-all fallback returns <c>true</c> for everything.
    /// </summary>
    bool CanHandle(string fileExtension);

    /// <summary>
    /// Selection priority when more than one provider <see cref="CanHandle"/>s an extension; the
    /// highest wins. Specialized providers use a non-negative value; the catch-all fallback uses
    /// <see cref="MarkdownProviderPriorities.Fallback"/> so any specialized provider outranks it.
    /// </summary>
    int Priority { get; }

    Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Well-known <see cref="IMarkdownTextProvider.Priority"/> values. Higher wins; the catch-all
/// fallback sits below every specialized provider.
/// </summary>
public static class MarkdownProviderPriorities
{
    /// <summary>The catch-all fallback (e.g. ElBruno). Below any specialized, extension-owning provider.</summary>
    public const int Fallback = int.MinValue;

    /// <summary>Default for a specialized provider that owns specific extensions (e.g. PDF).</summary>
    public const int Specialized = 0;
}
