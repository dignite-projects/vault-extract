using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration test (SQLite) for <see cref="IDocumentRepository.FindByContentHashAsync"/> — the #221
/// upload-time content-hash dedup check, scoped to the uploader by #655. The predicate on <c>CreatorId</c> is
/// asserted here against the real provider, because the application-layer tests substitute the repository and so
/// cannot see it.
/// </summary>
public class EfCoreDocumentRepositoryContentHash_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-0000-0000-000000000002");

    private readonly IDocumentRepository _documentRepository;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;

    public EfCoreDocumentRepositoryContentHash_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _currentPrincipalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    [Fact]
    public async Task FindByContentHashAsync_Finds_The_Callers_Own_Document_With_A_Matching_ContentHash()
    {
        var contentHash = NewHash();
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(() => InsertAsync(documentId, contentHash, Alice));

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Alice));

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(documentId);
    }

    [Fact]
    public async Task FindByContentHashAsync_Does_Not_Find_A_Document_Only_Another_User_Uploaded()
    {
        // #655: another uploader's copy of the same bytes is not a duplicate of yours — and must not be named to you.
        var contentHash = NewHash();
        await WithUnitOfWorkAsync(() => InsertAsync(Guid.NewGuid(), contentHash, Alice));

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Bob));

        found.ShouldBeNull();
    }

    [Fact]
    public async Task FindByContentHashAsync_Returns_The_Callers_Own_Copy_When_Several_Users_Uploaded_The_Same_Bytes()
    {
        var contentHash = NewHash();
        var aliceDocument = Guid.NewGuid();
        var bobDocument = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(aliceDocument, contentHash, Alice);
            await InsertAsync(bobDocument, contentHash, Bob);
        });

        var forAlice = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Alice));
        var forBob = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Bob));

        forAlice!.Id.ShouldBe(aliceDocument);
        forBob!.Id.ShouldBe(bobDocument);
    }

    [Fact]
    public async Task FindByContentHashAsync_Finds_The_Callers_Own_Recycle_Bin_Document()
    {
        // The check traverses soft delete: a re-upload of a file the caller already withdrew must surface as
        // InRecycleBin (which they can restore), not be silently accepted as new.
        var contentHash = NewHash();
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(documentId, contentHash, Alice);
            await _documentRepository.DeleteAsync(documentId, autoSave: true);
        });

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Alice));

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(documentId);
        found.IsDeleted.ShouldBeTrue();
    }

    [Fact]
    public async Task FindByContentHashAsync_Does_Not_Find_Another_Users_Recycle_Bin_Document()
    {
        var contentHash = NewHash();
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(documentId, contentHash, Alice);
            await _documentRepository.DeleteAsync(documentId, autoSave: true);
        });

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Bob));

        found.ShouldBeNull();
    }

    [Fact]
    public async Task FindByContentHashAsync_Null_Creator_Matches_Only_A_Null_CreatorId()
    {
        // #651 §2, read on the upload side: a caller with no user id (a client_credentials caller) matches only a
        // document that also has no CreatorId. `d.CreatorId == owner` with a null owner is `NULL = NULL` in plain
        // SQL, so whether the provider rewrites it to IS NULL is pinned here, not assumed.
        var contentHash = NewHash();
        var uploaderless = Guid.NewGuid();
        await WithUnitOfWorkAsync(() => InsertAsync(uploaderless, contentHash, creatorId: null));

        var forNull = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, null));
        var forUser = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, Alice));

        forNull.ShouldNotBeNull();
        forNull!.Id.ShouldBe(uploaderless);
        forUser.ShouldBeNull();
    }

    [Fact]
    public async Task FindByContentHashAsync_Null_Creator_Does_Not_Match_A_Document_With_A_CreatorId()
    {
        var contentHash = NewHash();
        await WithUnitOfWorkAsync(() => InsertAsync(Guid.NewGuid(), contentHash, Alice));

        var found = await WithUnitOfWorkAsync(() => _documentRepository.FindByContentHashAsync(contentHash, null));

        found.ShouldBeNull();
    }

    private static string NewHash() => $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64];

    private async Task InsertAsync(Guid documentId, string contentHash, Guid? creatorId)
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

        if (creatorId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId.Value);
        }

        // Insert under an ANONYMOUS principal, so `creatorId: null` really means a null CreatorId. ABP's audit
        // property setter fills CreatorId from CurrentUser.Id on insert whenever it is still null, and the test
        // base's FakeCurrentPrincipalAccessor always supplies a user id (see EfCoreDocumentRepositoryDuplicate_Tests).
        // An explicitly set CreatorId survives either way: the setter returns early when the value is already there.
        using (_currentPrincipalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity())))
        {
            await _documentRepository.InsertAsync(document, autoSave: true);
        }
    }
}
