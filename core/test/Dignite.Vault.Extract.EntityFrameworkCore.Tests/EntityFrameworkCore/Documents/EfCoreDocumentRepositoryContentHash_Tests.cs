using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration test (SQLite) for <see cref="IDocumentRepository.FindByContentHashAsync"/> — the #221
/// upload-time content-hash dedup check.
/// </summary>
public class EfCoreDocumentRepositoryContentHash_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private readonly IDocumentRepository _documentRepository;

    public EfCoreDocumentRepositoryContentHash_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
    }

    [Fact]
    public async Task FindByContentHashAsync_Finds_The_Document_With_A_Matching_ContentHash()
    {
        var contentHash = $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];
        var documentId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(
                documentId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{documentId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: contentHash,
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));
            await _documentRepository.InsertAsync(document, autoSave: true);
        });

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash));

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(documentId);
    }
}
