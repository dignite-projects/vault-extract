using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration test (SQLite) for <see cref="IDocumentRepository.FindByContentHashAsync"/> — the #221
/// upload-time content-hash dedup check, scoped by #481 to non-derived rows only (<c>OriginDocumentId == null</c>).
/// A derived sub-document shares its parent's <c>FileOrigin.ContentHash</c> wholesale (a text-slice child shares
/// the whole bundle's hash), so an unscoped check would nondeterministically report a child row as "the duplicate".
/// </summary>
public class EfCoreDocumentRepositoryContentHash_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;

    public EfCoreDocumentRepositoryContentHash_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task FindByContentHashAsync_Skips_A_Derived_Documents_Shared_Hash_But_Still_Matches_An_Identical_Non_Derived_Row()
    {
        var contentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
        var parentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var parent = new Document(
                parentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{parentId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: contentHash,
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));
            await _documentRepository.InsertAsync(parent, autoSave: true);

            // A text-slice child shares the parent's FileOrigin wholesale (#481), including ContentHash. CloneFileOrigin
            // (#485) builds an equal-valued (not same-reference) copy -- see its XML doc for why a same-reference
            // FileOrigin instance cannot be attached to two Document rows within one DbContext.
            var child = Document.CreateDerived(
                Guid.NewGuid(),
                tenantId: null,
                fileOrigin: CloneFileOrigin(parent.FileOrigin),
                originDocumentId: parentId,
                originConstituentKey: "slice-1");
            await _documentRepository.InsertAsync(child, autoSave: true);
        });

        // The scoped check (OriginDocumentId == null) resolves to the non-derived parent row, never the child,
        // even though both rows carry the identical ContentHash.
        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash));

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(parentId);
    }
}
