using System.Collections.Generic;
using Volo.Abp;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Native payload archive manifest (persisted value object, #210). Provider-native output has been
/// written to blob storage, and this class stores only the <b>metadata required to retrieve it</b>.
/// Raw bytes <b>do not enter the DB</b>. Serialized as part of
/// <see cref="DocumentParseMetadata.NativePayloadManifest"/> into the
/// <c>Document.ExtractionMetadata</c> JSON column.
/// <para>
/// Get-only properties plus a single parameterized constructor let System.Text.Json reuse this
/// constructor during deserialization, with parameter names matching property names. This follows the
/// same pattern as <c>ExportColumn</c>. When there is <b>no</b> archive,
/// <see cref="DocumentParseMetadata.NativePayloadManifest"/> is <c>null</c> as a whole
/// instead of an empty shell.
/// </para>
/// </summary>
public class NativePayloadManifest : ValueObject
{
    /// <summary>
    /// Stable per-document key for the archived blob (<c>extraction-native/{documentId}</c>, overwritten
    /// on re-extraction). This is an <b>internal storage key</b> and must <b>never</b> be exposed in
    /// outbound DTOs; without a download endpoint it would leak a storage key. Permanent delete uses
    /// this key to delete the archived blob.
    /// </summary>
    public string BlobName { get; }

    /// <summary>MIME type of the archived payload, for example <c>application/json</c>.</summary>
    public string ContentType { get; }

    /// <summary>Archived payload size in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>SHA-256 of the archived payload in lowercase hex, for integrity verification / deduplication.</summary>
    public string Sha256 { get; }

    /// <summary>Payload schema identifier, for example <c>PaddleOCR/PP-StructureV3</c>, used by consumers to choose a parser.</summary>
    public string SchemaName { get; }

    public NativePayloadManifest(string blobName, string contentType, long sizeBytes, string sha256, string schemaName)
    {
        BlobName = Check.NotNullOrWhiteSpace(blobName, nameof(blobName));
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType));
        SizeBytes = sizeBytes;
        Sha256 = Check.NotNullOrWhiteSpace(sha256, nameof(sha256));
        SchemaName = Check.NotNullOrWhiteSpace(schemaName, nameof(schemaName));
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return BlobName;
        yield return ContentType;
        yield return SizeBytes;
        yield return Sha256;
        yield return SchemaName;
    }
}
