using System.Collections.Generic;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Provenance metadata</b> for document text extraction (persisted value object, #210).
/// Serialized as a whole into the <c>Document.ExtractionMetadata</c> JSON column
/// (<c>AbpJsonValueConverter</c> plus a handwritten <c>ValueComparer</c>, following the
/// <c>ExportTemplate.Columns</c> pattern).
/// <para>
/// The <c>Text</c> class-name prefix disambiguates field extraction from text extraction.
/// </para>
/// <para>
/// Carries the winning provider name plus the native payload archive manifest
/// (<see cref="NativePayloadManifest"/>, nullable when nothing was archived). Raw spatial signals such
/// as bbox / cell data <b>stay in blob storage</b>; this class stores only the manifest.
/// </para>
/// <para>
/// Get-only properties plus a single parameterized constructor let System.Text.Json reuse this
/// constructor during deserialization, with parameter names matching property names. This follows the
/// same pattern as <c>ExportColumn</c>.
/// </para>
/// </summary>
public class DocumentParseMetadata : ValueObject
{
    /// <summary>
    /// Winning provider family / name, such as <c>PaddleOCR</c>,
    /// <c>AzureDocumentIntelligence</c>, or <c>ElBruno.MarkItDotNet</c>; null when unknown.
    /// <para>
    /// <b>Not a parser entry point.</b> When archived native payload is parsed later, choose by
    /// <see cref="NativePayloadManifest.SchemaName"/>, which reaches model granularity such as
    /// <c>PaddleOCR/PP-StructureV3</c>. Different models under the same provider can have different
    /// structures, for example PP-StructureV3 page-level bbox placeholders versus PP-OCRv4 line-level
    /// output, so a family name alone can select the wrong parser. This field is only the fallback
    /// "who produced the Markdown" provenance when payload is absent, such as digital-native input or
    /// archive failure with a null manifest.
    /// </para>
    /// </summary>
    public string? ProviderName { get; }

    /// <summary>Native payload archive manifest; <c>null</c> when not archived (no payload / over limit / write failed).</summary>
    public NativePayloadManifest? NativePayloadManifest { get; }

    /// <summary>
    /// Whether this text extraction is <b>complete</b> (#268). <c>true</c> means all content was
    /// captured; <c>false</c> means content is known to be missing, such as truncated OCR output,
    /// duplicate-guard drops, or pages in a multi-page PDF that could not be transcribed. Historical
    /// records created before this signal default to <c>true</c> and are treated as complete.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>Short diagnostic when incomplete; <c>null</c> when complete.</summary>
    public string? IncompleteReason { get; }

    /// <summary>
    /// Retained-figure blob manifest (#477): one entry per persisted embedded-figure <b>source image</b>, or
    /// <c>null</c> when figure retention was off / no figures were retained. Its only purpose is manifest-driven
    /// blob reclaim on permanent delete (ABP's <c>IBlobContainer</c> has no list-by-prefix, so — like
    /// <see cref="NativePayloadManifest"/> — reclaim deletes by stable key, never by prefix sweep). Raw bytes stay
    /// in blob storage; this stores only the entries. Historical records / retention-off runs default to <c>null</c>.
    /// </summary>
    public IReadOnlyList<FigureManifestEntry>? Figures { get; }

    public DocumentParseMetadata(
        string? providerName,
        NativePayloadManifest? nativePayloadManifest,
        bool isComplete = true,
        string? incompleteReason = null,
        IReadOnlyList<FigureManifestEntry>? figures = null)
    {
        ProviderName = providerName;
        NativePayloadManifest = nativePayloadManifest;
        IsComplete = isComplete;
        IncompleteReason = incompleteReason;
        Figures = figures;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        // ABP ValueEquals uses SequenceEqual + the default comparer, which applies object.Equals to
        // atomics. Nested ValueObject values do not recursively use ValueEquals; the default comparer
        // degrades to reference equality. Flatten NativePayloadManifest atomics into the parent
        // sequence so parent-level ValueEquals performs real structural deep comparison. Nullable
        // members use empty string / 0 placeholders to avoid null atomics, and a NULL sentinel
        // distinguishes "manifest absent" from "manifest fields all have default values".
        yield return ProviderName ?? string.Empty;

        // Emit completeness atomics before the manifest yield break; otherwise they would be skipped
        // when manifest is null and would not participate in equality.
        yield return IsComplete;
        yield return IncompleteReason ?? string.Empty;

        // #477 figures manifest — flattened BEFORE the native-manifest yield break below, so the atomics
        // participate in equality even when NativePayloadManifest is null (the common case: the PDF figure path
        // archives no native payload). A NULL sentinel distinguishes "no figures" from an empty list.
        if (Figures is null)
        {
            yield return "\0null-figures";
        }
        else
        {
            yield return "\0figures";
            yield return Figures.Count;
            foreach (var figure in Figures)
            {
                yield return figure.BlobName;
                yield return figure.ContentHash;
                yield return figure.ContentType;
                yield return figure.SizeBytes;
            }
        }

        if (NativePayloadManifest is null)
        {
            yield return "\0null-manifest";
            yield break;
        }

        yield return NativePayloadManifest.BlobName;
        yield return NativePayloadManifest.ContentType;
        yield return NativePayloadManifest.SizeBytes;
        yield return NativePayloadManifest.Sha256;
        yield return NativePayloadManifest.SchemaName;
    }
}
