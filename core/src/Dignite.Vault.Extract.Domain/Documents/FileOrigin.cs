using System.Collections.Generic;
using Volo.Abp;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// File origin information. Immutable after write and treated as a system-trusted anchor.
/// </summary>
public class FileOrigin : ValueObject
{
    /// <summary>Key in BlobStore, immutable after write.</summary>
    public string BlobName { get; private set; } = default!;

    /// <summary>Snapshot of uploader display name, redundantly stored to preserve information after user deletion.</summary>
    public string UploadedByUserName { get; private set; } = default!;

    /// <summary>Original file name.</summary>
    public string? OriginalFileName { get; private set; }

    /// <summary>File MIME type.</summary>
    public string ContentType { get; private set; } = default!;

    /// <summary>SHA-256 hash of file content in lowercase hex, length 64. Used for byte-level deduplication within each tenant.</summary>
    public string ContentHash { get; private set; } = default!;

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; private set; }

    protected FileOrigin() { }

    public FileOrigin(
        string blobName,
        string uploadedByUserName,
        string contentType,
        string contentHash,
        long fileSize,
        string? originalFileName = null)
    {
        BlobName = Check.NotNullOrWhiteSpace(blobName, nameof(blobName), FileOriginConsts.MaxBlobNameLength);
        UploadedByUserName = Check.NotNullOrWhiteSpace(
            uploadedByUserName,
            nameof(uploadedByUserName),
            FileOriginConsts.MaxUploadedByUserNameLength);
        ContentType = Check.NotNullOrWhiteSpace(contentType, nameof(contentType), FileOriginConsts.MaxContentTypeLength);
        ContentHash = Check.NotNullOrWhiteSpace(contentHash, nameof(contentHash), FileOriginConsts.MaxContentHashLength);
        FileSize = Check.Range(fileSize, nameof(fileSize), 0, long.MaxValue);
        OriginalFileName = NormalizeOptionalString(originalFileName, nameof(originalFileName), FileOriginConsts.MaxOriginalFileNameLength);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return BlobName;
        yield return UploadedByUserName;
        yield return OriginalFileName ?? string.Empty;
        yield return ContentType;
        yield return ContentHash;
        yield return FileSize;
    }

    private static string? NormalizeOptionalString(string? value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.Length > maxLength)
        {
            throw new AbpException($"{parameterName} can not be longer than {maxLength} characters.");
        }

        return value;
    }
}
