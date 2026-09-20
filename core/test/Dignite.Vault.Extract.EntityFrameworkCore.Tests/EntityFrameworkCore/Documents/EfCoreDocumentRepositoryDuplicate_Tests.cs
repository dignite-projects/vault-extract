using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Shouldly;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration tests (SQLite) for <see cref="IDocumentRepository.FindDuplicateCandidatesAsync"/> (#411):
/// the duplicate-detection collision query. Verifies it matches other documents in the same layer + type sharing a
/// fingerprint, excludes the document itself / different types / different fingerprints / soft-deleted documents, and
/// honors the hard result cap.
/// </summary>
public class EfCoreDocumentRepositoryDuplicate_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid TypeAId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1");
    private static readonly Guid TypeBId = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2");

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;

    public EfCoreDocumentRepositoryDuplicate_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _currentPrincipalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
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
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20, DuplicateDetectionScope.Layer, subjectCreatorId: null, DocumentAccessScope.Unrestricted));

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
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20, DuplicateDetectionScope.Layer, subjectCreatorId: null, DocumentAccessScope.Unrestricted));

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
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 2, DuplicateDetectionScope.Layer, subjectCreatorId: null, DocumentAccessScope.Unrestricted));

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

    /// <summary>
    /// #635: the candidates name other people's documents by title and file name, so the caller's read scope
    /// narrows them. Against the real provider, because the scope arrives as an EF predicate here rather than as
    /// a membership test.
    /// </summary>
    [Fact]
    public async Task Candidates_Are_Narrowed_By_The_Callers_Read_Scope()
    {
        var self = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var owner = Guid.Parse("11111111-1111-1111-1111-000000000635");

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(self, TypeAId, "fp-1", creatorId: owner);
            await InsertAsync(mine, TypeAId, "fp-1", creatorId: owner);
            await InsertAsync(theirs, TypeAId, "fp-1", creatorId: Guid.NewGuid());
        });

        // An owner-only uploader: no type in scope, their own id on the owner arm.
        var ownerScope = DocumentAccessScope.Of(new HashSet<Guid>(), owner);
        var ownerCandidates = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20, DuplicateDetectionScope.Layer, subjectCreatorId: owner, ownerScope));
        ownerCandidates.Select(c => c.Id).ShouldBe(new[] { mine });

        // A Read-grant holder on the type still sees both, which is what the grant means.
        var granted = DocumentAccessScope.Of(new HashSet<Guid> { TypeAId }, ownerId: null);
        var grantedCandidates = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20, DuplicateDetectionScope.Layer, subjectCreatorId: owner, granted));
        grantedCandidates.Select(c => c.Id).ShouldBe(new[] { mine, theirs }, ignoreOrder: true);

        // And a scope that reaches nothing returns nothing rather than everything.
        var nothing = DocumentAccessScope.Of(new HashSet<Guid>(), ownerId: null);
        (await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(self, TypeAId, "fp-1", maxResults: 20, DuplicateDetectionScope.Layer, subjectCreatorId: owner, nothing)))
            .ShouldBeEmpty();
    }

    // --- #651: the type's own detection scope -------------------------------

    /// <summary>
    /// #651 2: under <c>Uploader</c> a collision requires the same <c>CreatorId</c>. Against the real provider,
    /// because the anchor arrives as an EF predicate here rather than as an in-memory comparison.
    /// </summary>
    [Fact]
    public async Task Uploader_Scope_Matches_Only_The_Subjects_Own_Uploader()
    {
        var alice = Guid.Parse("11111111-1111-1111-1111-000000000651");
        var bob = Guid.Parse("22222222-2222-2222-2222-000000000651");
        var self = Guid.NewGuid();
        var alicesOtherCopy = Guid.NewGuid();
        var bobsCopy = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(self, TypeAId, "fp-1", creatorId: alice);
            await InsertAsync(alicesOtherCopy, TypeAId, "fp-1", creatorId: alice);
            await InsertAsync(bobsCopy, TypeAId, "fp-1", creatorId: bob);
        });

        // Uploader: only Alice's other copy collides -- Bob keeping his own copy of the same invoice is exactly
        // what this scope declares legitimate.
        var uploaderScoped = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(
                self, TypeAId, "fp-1", maxResults: 20,
                DuplicateDetectionScope.Uploader, subjectCreatorId: alice, DocumentAccessScope.Unrestricted));
        uploaderScoped.Select(c => c.Id).ShouldBe(new[] { alicesOtherCopy });

        // The default is unchanged: the same data collides layer-wide with both.
        var layerScoped = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(
                self, TypeAId, "fp-1", maxResults: 20,
                DuplicateDetectionScope.Layer, subjectCreatorId: alice, DocumentAccessScope.Unrestricted));
        layerScoped.Select(c => c.Id).ShouldBe(new[] { alicesOtherCopy, bobsCopy }, ignoreOrder: true);
    }

    /// <summary>
    /// #651 2, the part that must be pinned by a test rather than assumed: <c>d.CreatorId == owner</c> with a
    /// null <c>owner</c> is <c>NULL = NULL</c> in plain SQL, and whether the provider rewrites it to
    /// <c>IS NULL</c> is a null-semantics detail the predicate must not depend on silently. Both directions:
    /// a null subject matches only null candidates, and a non-null subject never matches a null one.
    /// </summary>
    [Fact]
    public async Task Uploader_Scope_Matches_A_Null_Uploader_Only_To_Another_Null_Uploader()
    {
        var named = Guid.Parse("33333333-3333-3333-3333-000000000651");
        var machineSelf = Guid.NewGuid();
        var machineOther = Guid.NewGuid();
        var namedCopy = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(machineSelf, TypeAId, "fp-1");                  // CreatorId null
            await InsertAsync(machineOther, TypeAId, "fp-1");                 // CreatorId null
            await InsertAsync(namedCopy, TypeAId, "fp-1", creatorId: named);
        });

        // A null subject reaches the other uploaderless row, and never the named one.
        var fromMachine = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(
                machineSelf, TypeAId, "fp-1", maxResults: 20,
                DuplicateDetectionScope.Uploader, subjectCreatorId: null, DocumentAccessScope.Unrestricted));
        fromMachine.Select(c => c.Id).ShouldBe(new[] { machineOther });

        // A named subject never reaches the uploaderless rows -- here that leaves nothing at all.
        var fromNamed = await WithUnitOfWorkAsync(() =>
            _documentRepository.FindDuplicateCandidatesAsync(
                namedCopy, TypeAId, "fp-1", maxResults: 20,
                DuplicateDetectionScope.Uploader, subjectCreatorId: named, DocumentAccessScope.Unrestricted));
        fromNamed.ShouldBeEmpty();
    }

    /// <summary>
    /// #651 5 step 1. The collision buckets count LIVE documents only, matching what
    /// <see cref="IDocumentRepository.FindDuplicateCandidatesAsync"/> can see, and group by the key the scope
    /// defines -- fingerprint alone under <c>Layer</c>, fingerprint + uploader under <c>Uploader</c>.
    /// </summary>
    [Fact]
    public async Task Collision_Counts_Group_By_Scope_And_Exclude_RecycleBin_Rows()
    {
        var alice = Guid.Parse("44444444-4444-4444-4444-000000000651");
        var bob = Guid.Parse("55555555-5555-5555-5555-000000000651");
        var alice1 = Guid.NewGuid();
        var alice2 = Guid.NewGuid();
        var bob1 = Guid.NewGuid();
        var binned = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(alice1, TypeAId, "fp-1", creatorId: alice);
            await InsertAsync(alice2, TypeAId, "fp-1", creatorId: alice);
            await InsertAsync(bob1, TypeAId, "fp-1", creatorId: bob);
            await InsertAsync(binned, TypeAId, "fp-1", creatorId: alice);
            await InsertAsync(Guid.NewGuid(), TypeAId, fingerprint: null);    // no key at all -> no bucket
        });

        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(binned));

        var layer = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountDuplicateCollisionsAsync(
                TypeAId, DuplicateDetectionScope.Layer, new[] { "fp-1" }));
        layer.Count.ShouldBe(1);
        layer[new DuplicateCollisionKey("fp-1", null)].ShouldBe(3);   // the recycle-bin row does not count

        var uploader = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountDuplicateCollisionsAsync(
                TypeAId, DuplicateDetectionScope.Uploader, new[] { "fp-1" }));
        uploader.Count.ShouldBe(2);
        uploader[new DuplicateCollisionKey("fp-1", alice)].ShouldBe(2);
        uploader[new DuplicateCollisionKey("fp-1", bob)].ShouldBe(1);
    }

    /// <summary>
    /// #651 5 step 1, the bound: the aggregate answers only about the fingerprints the caller names, because the
    /// reconciliation job asks once per page and a whole-type aggregate per page would be quadratic in the type's
    /// size. A fingerprint outside the set is not aggregated even though its documents are right there, and an
    /// empty set is answered without a query at all.
    /// </summary>
    [Fact]
    public async Task Collision_Counts_Are_Restricted_To_The_Named_Fingerprints()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(Guid.NewGuid(), TypeAId, "fp-wanted");
            await InsertAsync(Guid.NewGuid(), TypeAId, "fp-wanted");
            await InsertAsync(Guid.NewGuid(), TypeAId, "fp-other");
            await InsertAsync(Guid.NewGuid(), TypeAId, "fp-other");
        });

        var named = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountDuplicateCollisionsAsync(
                TypeAId, DuplicateDetectionScope.Layer, new[] { "fp-wanted" }));

        named.Count.ShouldBe(1);
        named[new DuplicateCollisionKey("fp-wanted", null)].ShouldBe(2);
        named.ContainsKey(new DuplicateCollisionKey("fp-other", null)).ShouldBeFalse();

        var none = await WithUnitOfWorkAsync(() =>
            _documentRepository.CountDuplicateCollisionsAsync(
                TypeAId, DuplicateDetectionScope.Layer, Array.Empty<string>()));
        none.ShouldBeEmpty();
    }

    /// <summary>
    /// #651 5 step 2, the opposite soft-delete scope from the counts one test up: the reconciliation projection
    /// INCLUDES recycle-bin rows, or restoring a document would bring back a flag derived from a retracted rule.
    /// </summary>
    [Fact]
    public async Task Reconciliation_Projection_Includes_RecycleBin_Rows()
    {
        var live = Guid.NewGuid();
        var binned = Guid.NewGuid();
        var owner = Guid.Parse("66666666-6666-6666-6666-000000000651");

        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            await InsertAsync(live, TypeAId, "fp-1", creatorId: owner);
            await InsertAsync(binned, TypeAId, "fp-1", creatorId: owner);
        });

        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(binned));

        var rows = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetDuplicateReconciliationPageAsync(TypeAId, afterId: null, maxCount: 100));

        rows.Select(r => r.Id).ShouldContain(binned);
        var binnedRow = rows.Single(r => r.Id == binned);
        binnedRow.IsDeleted.ShouldBeTrue();
        binnedRow.FieldFingerprint.ShouldBe("fp-1");
        binnedRow.CreatorId.ShouldBe(owner);
        rows.Single(r => r.Id == live).IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Reconciliation_Projection_Pages_By_Keyset()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await EnsureTypeAsync(TypeAId, "type.a");
            for (var i = 0; i < 5; i++)
            {
                await InsertAsync(Guid.NewGuid(), TypeAId, "fp-1");
            }
        });

        var first = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetDuplicateReconciliationPageAsync(TypeAId, afterId: null, maxCount: 2));
        first.Count.ShouldBe(2);

        var second = await WithUnitOfWorkAsync(() =>
            _documentRepository.GetDuplicateReconciliationPageAsync(TypeAId, afterId: first[^1].Id, maxCount: 2));
        second.Count.ShouldBe(2);
        second.Select(r => r.Id).ShouldNotContain(first[0].Id);
        second[0].Id.CompareTo(first[^1].Id).ShouldBeGreaterThan(0);
    }

    private async Task InsertAsync(
        Guid id,
        Guid documentTypeId,
        string? fingerprint,
        Guid? creatorId = null,
        bool flagDuplicate = false,
        bool allowDuplicate = false)
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
        if (flagDuplicate)
        {
            doc.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
        }

        if (allowDuplicate)
        {
            // Through the aggregate, so the seeded state is exactly what an operator override leaves behind.
            doc.AllowDuplicate();
        }

        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(doc, creatorId.Value);
        }

        // Insert under an ANONYMOUS principal, so `creatorId: null` really means a null CreatorId. ABP's audit
        // property setter fills CreatorId from CurrentUser.Id on insert whenever it is still null, and the test
        // base's FakeCurrentPrincipalAccessor always supplies a user id — without this, every "uploaderless"
        // row would silently be created by `admin`, and #651's null-to-null cases would be untestable (they
        // first failed exactly this way). An explicitly set CreatorId survives either way: the setter returns
        // early when the value is already there. This is also the real shape of the rows in question — a
        // client_credentials caller has no `sub`.
        using (_currentPrincipalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity())))
        {
            await _documentRepository.InsertAsync(doc, autoSave: true);
        }
    }
}
