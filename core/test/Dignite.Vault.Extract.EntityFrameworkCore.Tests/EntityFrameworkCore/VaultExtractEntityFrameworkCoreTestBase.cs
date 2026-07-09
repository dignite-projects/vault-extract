using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.EntityFrameworkCore;

/* This class can be used as a base class for EF Core integration tests.
 */
public abstract class VaultExtractEntityFrameworkCoreTestBase : VaultExtractTestBase<VaultExtractEntityFrameworkCoreTestModule>
{
    /// <summary>
    /// #485: builds an EQUAL-VALUED (not same-reference) copy of <paramref name="source"/>'s 6 fields, for tests
    /// that seed a parent Document plus a derived child SHARING the parent's <see cref="FileOrigin"/> (#481) within
    /// the same DbContext / UoW. EF Core's owned-entity change tracker does not allow attaching the SAME tracked
    /// <see cref="FileOrigin"/> CLR instance to two <see cref="Document"/> rows (its shadow key includes the
    /// owner's Id) — production code never hits this because the parent is loaded in an earlier, already-disposed
    /// UoW/DbContext before a derived document's own UoW begins (mirrors the same reasoning behind
    /// <c>DocumentSegmentationJob</c>'s #485 fix, which constructs a fresh <see cref="FileOrigin"/> for exactly
    /// this reason on its Text-kind spawn path). Shared by <c>EfCoreDocumentRepositoryContentHash_Tests</c> and
    /// <c>EfCoreDocumentRepositoryStatistics_Tests</c> so neither hand-clones <see cref="FileOrigin"/> field-by-field.
    /// </summary>
    protected static FileOrigin CloneFileOrigin(FileOrigin source)
        => new(
            blobName: source.BlobName,
            uploadedByUserName: source.UploadedByUserName,
            contentType: source.ContentType,
            contentHash: source.ContentHash,
            fileSize: source.FileSize,
            originalFileName: source.OriginalFileName);
}
