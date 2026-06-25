using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration tests (SQLite) for <see cref="IDocumentRepository.FindDuplicateCandidatesAsync"/> (#411):
/// the duplicate-detection collision query. Verifies it matches other documents in the same layer + type sharing a
/// fingerprint, excludes the document itself / different types / different fingerprints / soft-deleted documents, and
/// honors the hard result cap.
/// </summary>
public class EfCoreDocumentRepositoryDuplicate_Tests : ExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid TypeAId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid TypeBId = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2");

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;

    public EfCoreDocumentRepositoryDuplicate_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
    }

    [Fact]
    public async Task Finds_Other_Same_Type_Same_Fingerprint_Excluding_Self_Type_And_Fingerprint()
    {
        var self = Guid.NewGuid();
        var collides = Guid.NewGuid();
        var otherFingerprint = Guid.NewGuid();
        var otherType = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await EnsureTypeAsync(TypeBId, "type.b");
            await InsertAsync(self, TypeAId, "fp-1");
            await InsertAsync(collides, TypeAId, "fp-1");          // same type + fingerprint -> candidate
            await InsertAsync(otherFingerprint, TypeAId, "fp-2");  // same type, different fingerprint
            await InsertAsync(otherType, TypeBId, "fp-1");         // same fingerprint, different type
        });

        var candidates = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20));

        candidates.Select(c => c.Id).ShouldBe(new[] { collides });
        // The projection carries the recognizable fields the operator UI shows.
        candidates[0].Title.ShouldBe("Title " + collides.ToString("N")[..8]);
        candidates[0].FileName.ShouldBe("test.pdf");
    }

    [Fact]
    public async Task Excludes_SoftDeleted_Candidate()
    {
        var self = Guid.NewGuid();
        var collides = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(self, TypeAId, "fp-1");
            await InsertAsync(collides, TypeAId, "fp-1");
        });

        // Soft-delete the colliding document; the query honors the ISoftDelete global filter.
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(collides));

        var candidates = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20));

        candidates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Honors_MaxResults_Cap()
    {
        var self = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(self, TypeAId, "fp-1");
            for (var i = 0; i < 3; i++)
            {
                await InsertAsync(Guid.NewGuid(), TypeAId, "fp-1");
            }
        });

        var candidates = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 2));

        candidates.Count.ShouldBe(2);
    }

    private async Task EnsureTypeAsync(Guid id, string code)
    {
        if (await _documentTypeRepository.FindAsync(id) == null)
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(id, tenantId: null, typeCode: code, displayName: code), autoSave: true);
        }
    }

    private async Task InsertAsync(Guid id, Guid documentTypeId, string fingerprint)
    {
        var doc = new Document(
            id, tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
        doc.SetMarkdown("# Body");
        doc.SetTitle("Title " + id.ToString("N")[..8]);
        // Assign the type first (ApplyAutomaticClassificationResult resets duplicate-detection state), then set the
        // fingerprint as the field extraction stage would.
        doc.ApplyAutomaticClassificationResult(documentTypeId, 0.99);
        doc.SetFieldFingerprint(fingerprint);
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }
}
