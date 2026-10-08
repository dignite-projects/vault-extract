using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

/// <summary>
/// Real EF integration test (SQLite) for <see cref="IDocumentRepository.AnonymizeUploaderAsync"/> (#698, GDPR). The
/// Application tests substitute the repository, so the predicate, the soft-delete traversal, the tenant scoping and
/// the "one UPDATE, no audit stamping" property are asserted here against the real provider.
/// </summary>
public class EfCoreDocumentRepositoryAnonymizeUploader_Tests : VaultExtractEntityFrameworkCoreTestBase
{
    private static readonly Guid Alice = Guid.Parse("a11ce000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("b0b00000-0000-0000-0000-000000000002");
    private static readonly Guid Carol = Guid.Parse("ca401000-0000-0000-0000-000000000003");
    private static readonly Guid TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly IDocumentRepository _documentRepository;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;

    public EfCoreDocumentRepositoryAnonymizeUploader_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _currentPrincipalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    [Fact]
    public async Task Erases_the_name_on_every_document_the_user_uploaded_and_changes_nothing_else()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var bobs = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(first, Alice, "Alice Example");
            await InsertAsync(second, Alice, "Alice Example");
            await InsertAsync(bobs, Bob, "Bob Example");
        });
        var before = await ReadAsync(first);

        var changed = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        changed.ShouldBe(2);
        var after = await ReadAsync(first);
        after.FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
        (await ReadAsync(second)).FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
        // Only the name moves: the rest of the anchor, and who owns the document, stay put.
        after.FileOrigin.BlobName.ShouldBe(before.FileOrigin!.BlobName);
        after.FileOrigin.ContentHash.ShouldBe(before.FileOrigin.ContentHash);
        after.FileOrigin.ContentType.ShouldBe(before.FileOrigin.ContentType);
        after.FileOrigin.FileSize.ShouldBe(before.FileOrigin.FileSize);
        after.FileOrigin.OriginalFileName.ShouldBe(before.FileOrigin.OriginalFileName);
        after.CreatorId.ShouldBe(Alice);
        // Another user's document is not touched.
        (await ReadAsync(bobs)).FileOrigin!.UploadedByUserName.ShouldBe("Bob Example");
    }

    [Fact]
    public async Task Reaches_a_document_in_the_recycle_bin()
    {
        // A soft-deleted document still carries the name and can be restored; leaving it behind would leave the
        // erasure undone for exactly the documents nobody looks at.
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertAsync(documentId, Alice, "Alice Example");
            await _documentRepository.DeleteAsync(documentId, autoSave: true);
        });

        var changed = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        changed.ShouldBe(1);
        var document = await ReadAsync(documentId);
        document.IsDeleted.ShouldBeTrue();
        document.FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
    }

    [Fact]
    public async Task Does_not_stamp_the_document_as_modified()
    {
        // Saving the aggregate would run ABP's audit stamping with no current user: LastModifierId would be nulled and
        // LastModificationTime set, erasing the trace of whoever else edited the document. The single UPDATE must
        // leave both alone.
        var documentId = Guid.NewGuid();
        var editedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await WithUnitOfWorkAsync(() => InsertAsync(documentId, Alice, "Alice Example", lastModifierId: Carol, lastModificationTime: editedAt));

        await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        var document = await ReadAsync(documentId);
        document.LastModifierId.ShouldBe(Carol);
        document.LastModificationTime.ShouldBe(editedAt);
    }

    [Fact]
    public async Task A_save_of_an_aggregate_loaded_before_the_erasure_fails_instead_of_restoring_the_name()
    {
        // ABP's UpdateAsync marks every column of the aggregate modified, owned FileOrigin columns included. A document
        // a pipeline job loaded before the erasure and saves after it would write the old name straight back, unless
        // the erasure moves the ConcurrencyStamp and so makes that stale save lose.
        var documentId = Guid.NewGuid();
        await WithUnitOfWorkAsync(() => InsertAsync(documentId, Alice, "Alice Example"));
        var stale = await ReadAsync(documentId); // detached once its unit of work ends: old name, old stamp

        await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        await Should.ThrowAsync<AbpDbConcurrencyException>(
            () => WithUnitOfWorkAsync(() => _documentRepository.UpdateAsync(stale, autoSave: true)));
        (await ReadAsync(documentId)).FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        // The event is delivered at least once: a redelivery must be a quiet no-op.
        await WithUnitOfWorkAsync(() => InsertAsync(Guid.NewGuid(), Alice, "Alice Example"));
        (await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice))).ShouldBe(1);

        var again = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        again.ShouldBe(0);
    }

    [Fact]
    public async Task A_user_with_no_documents_is_a_no_op()
    {
        await WithUnitOfWorkAsync(() => InsertAsync(Guid.NewGuid(), Alice, "Alice Example"));

        var changed = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Guid.NewGuid()));

        changed.ShouldBe(0);
    }

    [Fact]
    public async Task Skips_a_derived_document_that_has_no_file_origin()
    {
        // A sub-document has no FileOrigin of its own (it is a Markdown slice), so there is no name to erase and the
        // `FileOrigin != null` guard has to keep the statement off it.
        var derived = Guid.NewGuid();
        var upload = Guid.NewGuid();
        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(
                Document.CreateDerived(derived, tenantId: null, fileOrigin: null, Guid.NewGuid(), "segment-1", Alice),
                autoSave: true);
            await InsertAsync(upload, Alice, "Alice Example");
        });

        var changed = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        changed.ShouldBe(1);
        (await ReadAsync(derived)).FileOrigin.ShouldBeNull();
        (await ReadAsync(upload)).FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
    }

    [Fact]
    public async Task Erases_only_in_the_ambient_layer()
    {
        // The same account can have documents in the Host and in a tenant. The repository works one layer at a time
        // (IMultiTenant applies by ambient state) and the handler walks the layers, so each call must leave the
        // other layer's rows alone.
        var hostDocument = Guid.NewGuid();
        var tenantDocument = Guid.NewGuid();
        await WithUnitOfWorkAsync(() => InsertAsync(hostDocument, Alice, "Alice Example"));
        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                await InsertAsync(tenantDocument, Alice, "Alice Example", tenantId: TenantId);
            }
        });

        var inHost = await WithUnitOfWorkAsync(() => _documentRepository.AnonymizeUploaderAsync(Alice));

        inHost.ShouldBe(1);
        (await ReadAsync(hostDocument)).FileOrigin!.UploadedByUserName.ShouldBe(FileOriginConsts.AnonymizedUploaderName);
        (await ReadAsync(tenantDocument, TenantId)).FileOrigin!.UploadedByUserName.ShouldBe("Alice Example");

        var inTenant = await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(TenantId))
            {
                return await _documentRepository.AnonymizeUploaderAsync(Alice);
            }
        });

        inTenant.ShouldBe(1);
        (await ReadAsync(tenantDocument, TenantId)).FileOrigin!.UploadedByUserName
            .ShouldBe(FileOriginConsts.AnonymizedUploaderName);
    }

    private async Task<Document> ReadAsync(Guid documentId, Guid? tenantId = null)
    {
        // A fresh unit of work, so the row is read back from the database and not from a tracked instance, and with
        // the soft-delete filter off so a recycle-bin row can be read too.
        return await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(tenantId))
            using (_dataFilter.Disable<ISoftDelete>())
            {
                return await _documentRepository.GetAsync(documentId);
            }
        });
    }

    private async Task InsertAsync(
        Guid documentId,
        Guid creatorId,
        string uploadedByUserName,
        Guid? tenantId = null,
        Guid? lastModifierId = null,
        DateTime? lastModificationTime = null)
    {
        var document = new Document(
            documentId,
            tenantId,
            new FileOrigin(
                blobName: $"blobs/{documentId:N}.pdf",
                uploadedByUserName: uploadedByUserName,
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                fileSize: 2048,
                originalFileName: "bundle.pdf"));

        typeof(Document).GetProperty(nameof(Document.CreatorId))!.SetValue(document, creatorId);
        if (lastModifierId.HasValue)
        {
            document.LastModifierId = lastModifierId;
            document.LastModificationTime = lastModificationTime;
        }

        // An anonymous principal, so nothing but the explicit values below reaches the row: the test base's fake
        // principal always supplies a user id, which ABP's audit setter would otherwise use.
        using (_currentPrincipalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity())))
        {
            await _documentRepository.InsertAsync(document, autoSave: true);
        }
    }
}
