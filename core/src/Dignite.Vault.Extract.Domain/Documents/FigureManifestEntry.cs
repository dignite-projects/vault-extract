using System.Collections.Generic;
using Volo.Abp;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// One retained-figure blob's manifest entry (persisted value object, #477). When figure retention is enabled,
/// the parse job writes each embedded figure's <b>source image</b> to blob storage and records the metadata
/// required to retrieve and reclaim it here. Raw bytes <b>do not enter the DB</b>. Serialized as part of
/// <see cref="DocumentParseMetadata.Figures"/> into the <c>Document.ExtractionMetadata</c> JSON column, so
/// <see cref="Documents.DocumentAppService"/> can reclaim every figure blob on permanent delete by its
/// <see cref="BlobName"/> — ABP's <c>IBlobContainer</c> has no list-by-prefix, so reclaim is manifest-driven
/// exactly like <see cref="NativePayloadManifest"/> (no prefix cleanup).
/// <para>
/// Get-only properties plus a single parameterized constructor let System.Text.Json reuse this constructor
/// during deserialization, with parameter names matching property names — the same pattern as
/// <see cref="NativePayloadManifest"/> and <c>ExportColumn</c>.
/// </para>
/// </summary>
public class FigureManifestEntry : ValueObject
{
    /// <summary>
    /// Stable blob key (<c>extraction-figures/{documentId}/{contentHash}</c>, overwritten on re-extraction). An
    /// <b>internal storage key</b> — never exposed in outbound DTOs; permanent delete uses it to delete the blob.
    /// </summary>
    public string BlobName { get; }

    /// <summary>SHA-256 of the image bytes in lowercase hex — the dedup key and the <c>figures/{hash}</c> reference
    /// target inlined into <c>Document.Markdown</c>.</summary>
    public string ContentHash { get; }

    /// <summary>Image MIME type, for example <c>image/png</c> — the content type the egress serves the blob with.</summary>
    public string ContentType { get; }

    /// <summary>Retained image size in bytes.</summary>
    public long SizeBytes { get; }

    public FigureManifestEntry(string blobName, string contentHash, string contentType, long sizeBytes)
    {
        BlobName = Check.NotNullOrWhiteSpace(blobName, nameof(blobName));
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash));
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType));
        SizeBytes = sizeBytes;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return BlobName;
        yield return ContentHash;
        yield return ContentType;
        yield return SizeBytes;
    }
}
