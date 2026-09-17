using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Authorization;
using Volo.Abp.Content;
using Volo.Abp.Guids;
using Volo.Abp.PermissionManagement;
using Volo.Abp.Security.Claims;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// #632 acceptance, the real-chain half: one fact per per-document-type grant — <c>Upload</c> on
/// <c>UploadAsync</c>, <c>Read</c> on <c>GetAsync</c>, <c>Edit</c> on <c>ConfirmClassificationAsync</c>,
/// <c>Delete</c> on <c>DeleteAsync</c> and on <c>RestoreAsync</c> — each driven by a real row in the SQLite
/// <c>AbpResourcePermissionGrants</c> table, through ABP's real <c>IResourcePermissionChecker</c> and its real
/// user value provider.
/// <para>
/// This is the only test host in the repo with a real permission pipeline: <see cref="McpPermissionPipelineTestModule"/>
/// does <b>not</b> call <c>AddAlwaysAllowAuthorization</c> and registers no fake authorization service, so nothing
/// here is stubbed above the store. That matters because the #629 leftover it closes was precisely "the resource OR
/// has only ever been exercised against an in-memory store behind a hand-written authorization service".
/// </para>
/// <para>
/// Each fact also carries its negative: the same call, with the grant on the <b>other</b> type, must throw
/// <see cref="AbpAuthorizationException"/>. Without that half a grant check that always returned <c>true</c> would
/// pass every one of these.
/// </para>
/// </summary>
public class McpPerTypeGrantPipeline_Tests : McpPermissionPipelineTestBase<McpPermissionPipelineTestModule>
{
    // Distinct per fact: the SQLite connection lives for the app lifetime, so grants written by one fact would
    // otherwise be visible to the next (see McpPermissionPipelineTestModule).
    private static readonly Guid UploaderId = Guid.Parse("11111111-0000-0000-0000-000000000632");
    private static readonly Guid ReaderId = Guid.Parse("22222222-0000-0000-0000-000000000632");
    private static readonly Guid EditorId = Guid.Parse("33333333-0000-0000-0000-000000000632");
    private static readonly Guid DeleterId = Guid.Parse("44444444-0000-0000-0000-000000000632");
    private static readonly Guid RestorerId = Guid.Parse("55555555-0000-0000-0000-000000000632");

    private const string UserProviderName = "U";

    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IPermissionGrantRepository _permissionGrantRepository;
    private readonly IResourcePermissionGrantRepository _resourcePermissionGrantRepository;
    private readonly IGuidGenerator _guidGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;

    public McpPerTypeGrantPipeline_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _permissionGrantRepository = GetRequiredService<IPermissionGrantRepository>();
        _resourcePermissionGrantRepository = GetRequiredService<IResourcePermissionGrantRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
    }

    /// <summary>
    /// #629's rule, now exercised through the real chain rather than an in-memory store: an uploader holding
    /// <c>Documents.Upload</c> and no <c>ConfirmClassification</c> may declare exactly the type it was granted.
    /// </summary>
    [Fact]
    public async Task Upload_grant_on_a_type_admits_declaring_that_type_and_only_that_type()
    {
        Guid typeA = default, typeB = default;
        await WithUnitOfWorkAsync(async () =>
        {
            typeA = await SeedTypeAsync("per.type.upload.a");
            typeB = await SeedTypeAsync("per.type.upload.b");
            await GrantAsync(UploaderId, VaultExtractPermissions.Documents.Default);
            await GrantAsync(UploaderId, VaultExtractPermissions.Documents.Upload);
            await GrantResourceAsync(UploaderId, VaultExtractResourcePermissions.Upload, typeA);
        });

        using (_principalAccessor.Change(Principal(UploaderId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var created = await _documentAppService.UploadAsync(NewUpload("granted.txt", typeA));
                created.DocumentTypeCode.ShouldBe("per.type.upload.a");
            });

            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.UploadAsync(NewUpload("denied.txt", typeB))));
        }
    }

    /// <summary>
    /// #632 Read: a caller with <c>Documents.Default</c> (entry) and a <c>Read</c> grant on one type, and
    /// <b>no</b> <c>Documents.ReadAll</c>, reads that type's documents and is denied on every other type — and on
    /// untyped documents, which belong to no type and are therefore module-wide only.
    /// </summary>
    [Fact]
    public async Task Read_grant_on_a_type_admits_GetAsync_for_that_type_only()
    {
        Guid documentA = default, documentB = default, untyped = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeA = await SeedTypeAsync("per.type.read.a");
            var typeB = await SeedTypeAsync("per.type.read.b");
            documentA = await SeedDocumentAsync(typeA);
            documentB = await SeedDocumentAsync(typeB);
            untyped = await SeedDocumentAsync(documentTypeId: null);

            await GrantAsync(ReaderId, VaultExtractPermissions.Documents.Default);
            await GrantResourceAsync(ReaderId, VaultExtractResourcePermissions.Read, typeA);
        });

        using (_principalAccessor.Change(Principal(ReaderId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var dto = await _documentAppService.GetAsync(documentA);
                dto.Id.ShouldBe(documentA);
            });

            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.GetAsync(documentB)));

            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.GetAsync(untyped)));
        }
    }

    /// <summary>
    /// #632 Edit: an <c>Edit</c> grant on the document's current type stands in for the module-wide
    /// <c>Documents.ConfirmClassification</c>. Confirming <b>to</b> a type is a second, separate check against the
    /// target type, so this caller also holds the <c>Upload</c> grant on it; the negative half shows the Edit
    /// grant alone does not reach a document of another type.
    /// </summary>
    [Fact]
    public async Task Edit_grant_on_a_type_admits_ConfirmClassificationAsync_for_that_type_only()
    {
        Guid documentA = default, documentB = default, typeA = default;
        await WithUnitOfWorkAsync(async () =>
        {
            typeA = await SeedTypeAsync("per.type.edit.a");
            var typeB = await SeedTypeAsync("per.type.edit.b");
            documentA = await SeedDocumentAsync(typeA, markdown: "# A");
            documentB = await SeedDocumentAsync(typeB, markdown: "# B");

            await GrantAsync(EditorId, VaultExtractPermissions.Documents.Default);
            await GrantResourceAsync(EditorId, VaultExtractResourcePermissions.Edit, typeA);
            // The TARGET-type half of Confirm / Reclassify, which is the #629 Upload rule, not Edit.
            await GrantResourceAsync(EditorId, VaultExtractResourcePermissions.Upload, typeA);
        });

        using (_principalAccessor.Change(Principal(EditorId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var dto = await _documentAppService.ConfirmClassificationAsync(
                    documentA, new ConfirmClassificationInput { DocumentTypeId = typeA });

                // #635: this caller holds Edit and Upload on the type but no Read grant and no ReadAll, so the
                // edit succeeds and the response body is redacted to the id and the rights — through the REAL
                // permission chain, not a fake. Reading the type code off the response would have been the read
                // GetAsync refuses this principal.
                dto.Id.ShouldBe(documentA);
                dto.Rights.CanEdit.ShouldBeTrue();
                dto.Rights.CanRead.ShouldBeFalse();
                dto.DocumentTypeCode.ShouldBeNull();
                dto.FileOrigin.ShouldBeNull();
            });

            // The write itself landed, which is what the Edit grant is for.
            await WithUnitOfWorkAsync(async () =>
            {
                var reloaded = await _documentRepository.GetAsync(documentA);
                reloaded.DocumentTypeId.ShouldBe(typeA);
            });

            // Same target type (granted), but the document being edited is of type B — denied on the current-type
            // half of the rule.
            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.ConfirmClassificationAsync(
                    documentB, new ConfirmClassificationInput { DocumentTypeId = typeA })));
        }
    }

    /// <summary>
    /// #632 Delete: a <c>Delete</c> grant on one type soft-deletes that type's documents and nothing else. The
    /// caller holds no <c>Documents.Delete</c>, so the module-wide half of the OR contributes nothing here.
    /// </summary>
    [Fact]
    public async Task Delete_grant_on_a_type_admits_DeleteAsync_for_that_type_only()
    {
        Guid documentA = default, documentB = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeA = await SeedTypeAsync("per.type.delete.a");
            var typeB = await SeedTypeAsync("per.type.delete.b");
            documentA = await SeedDocumentAsync(typeA);
            documentB = await SeedDocumentAsync(typeB);

            await GrantAsync(DeleterId, VaultExtractPermissions.Documents.Default);
            await GrantResourceAsync(DeleterId, VaultExtractResourcePermissions.Delete, typeA);
        });

        using (_principalAccessor.Change(Principal(DeleterId)))
        {
            await WithUnitOfWorkAsync(() => _documentAppService.DeleteAsync(documentA));

            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.DeleteAsync(documentB)));
        }

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.FindAsync(documentA)).ShouldBeNull();
            (await _documentRepository.FindAsync(documentB)).ShouldNotBeNull();
        });
    }

    /// <summary>
    /// #632 change 2, through the real chain: the same <c>Delete</c> grant that admits <c>DeleteAsync</c> admits
    /// <c>RestoreAsync</c> — whoever may delete may undo. There is no <c>Restore</c> resource permission to grant,
    /// and this caller holds no module-wide <c>Documents.Restore</c>, so the grant row is doing all the work.
    /// </summary>
    [Fact]
    public async Task Delete_grant_on_a_type_admits_RestoreAsync_for_that_type_only()
    {
        Guid documentA = default, documentB = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var typeA = await SeedTypeAsync("per.type.restore.a");
            var typeB = await SeedTypeAsync("per.type.restore.b");
            documentA = await SeedDocumentAsync(typeA);
            documentB = await SeedDocumentAsync(typeB);

            await GrantAsync(RestorerId, VaultExtractPermissions.Documents.Default);
            await GrantResourceAsync(RestorerId, VaultExtractResourcePermissions.Delete, typeA);
        });

        // Both land in the recycle bin out of band, so the fact below is about Restore alone.
        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.DeleteAsync(documentA, autoSave: true);
            await _documentRepository.DeleteAsync(documentB, autoSave: true);
        });

        using (_principalAccessor.Change(Principal(RestorerId)))
        {
            await WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentA));

            await Should.ThrowAsync<AbpAuthorizationException>(() =>
                WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentB)));
        }

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.FindAsync(documentA)).ShouldNotBeNull();
            (await _documentRepository.FindAsync(documentB)).ShouldBeNull();
        });
    }

    /// <summary>
    /// #635: the grant map sweeps the layer's types <b>across soft delete</b>, so a <c>Read</c> grant on a type
    /// that has since been archived still reaches that type's documents. Otherwise the narrow caller and a
    /// <c>Documents.ReadAll</c> holder would disagree about which rows exist — archiving a type is a schema
    /// decision, not a revocation.
    /// <para>
    /// A real-provider fact: the Application-layer rights test that claims the same thing stubs
    /// <c>IDocumentTypeRepository</c>, so the <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> the claim rests on is
    /// never exercised there — a substitute returns the archived type whether the filter is disabled or not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Read_grant_on_an_archived_type_still_reaches_that_types_documents()
    {
        var archivedReaderId = Guid.Parse("66666666-0000-0000-0000-000000000635");
        Guid documentId = default;

        await WithUnitOfWorkAsync(async () =>
        {
            var typeId = await SeedTypeAsync("per.type.archived");
            documentId = await SeedDocumentAsync(typeId);

            await GrantAsync(archivedReaderId, VaultExtractPermissions.Documents.Default);
            await GrantResourceAsync(archivedReaderId, VaultExtractResourcePermissions.Read, typeId);

            // Archive the type AFTER the grant, exactly as an operator would.
            await _documentTypeRepository.DeleteAsync(typeId, autoSave: true);
        });

        using (_principalAccessor.Change(Principal(archivedReaderId)))
        {
            await WithUnitOfWorkAsync(async () =>
            {
                var page = await _documentAppService.GetListAsync(new GetDocumentListInput());
                page.TotalCount.ShouldBe(1);
                page.Items[0].Id.ShouldBe(documentId);
                page.Items[0].Rights.CanRead.ShouldBeTrue();

                (await _documentAppService.GetAsync(documentId)).Id.ShouldBe(documentId);
            });
        }
    }

    // ---- seeding / grant helpers ----

    private async Task<Guid> SeedTypeAsync(string typeCode)
    {
        var id = Guid.NewGuid();
        await _documentTypeRepository.InsertAsync(
            new DocumentType(id, tenantId: null, typeCode, typeCode), autoSave: true);
        return id;
    }

    /// <summary>
    /// Seeds one document. <c>DocumentTypeId</c> and <c>Markdown</c> both have Domain-private setters and this
    /// assembly is not in the Domain's <c>InternalsVisibleTo</c> list, so both are set by reflection — the same
    /// shape <see cref="McpPermissionResolution_Tests"/> already uses for the classified state.
    /// </summary>
    private async Task<Guid> SeedDocumentAsync(Guid? documentTypeId, string? markdown = null)
    {
        var document = new Document(
            Guid.NewGuid(),
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "svc",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "per-type.pdf"));

        if (documentTypeId.HasValue)
        {
            typeof(Document).GetProperty(nameof(Document.DocumentTypeId))!.SetValue(document, documentTypeId.Value);
        }

        if (markdown != null)
        {
            typeof(Document).GetProperty(nameof(Document.Markdown))!.SetValue(document, markdown);
        }

        await _documentRepository.InsertAsync(document, autoSave: true);
        return document.Id;
    }

    /// <summary>A standard permission grant, provider "U" (direct user grant), written straight to the store.</summary>
    private async Task GrantAsync(Guid userId, string permissionName)
    {
        await _permissionGrantRepository.InsertAsync(
            new PermissionGrant(
                _guidGenerator.Create(), permissionName, UserProviderName, userId.ToString(), tenantId: null),
            autoSave: true);
    }

    /// <summary>
    /// A row in <c>AbpResourcePermissionGrants</c> — the real table, keyed by the type's immutable Id, resolved by
    /// ABP's own <c>UserResourcePermissionValueProvider</c>. Nothing about the per-type half of the rule is faked.
    /// </summary>
    private async Task GrantResourceAsync(Guid userId, string permissionName, Guid documentTypeId)
    {
        await _resourcePermissionGrantRepository.InsertAsync(
            new ResourcePermissionGrant(
                _guidGenerator.Create(),
                permissionName,
                VaultExtractResourcePermissions.Name,
                documentTypeId.ToString(),
                UserProviderName,
                userId.ToString(),
                tenantId: null),
            autoSave: true);
    }

    private static UploadDocumentInput NewUpload(string fileName, Guid documentTypeId)
    {
        return new UploadDocumentInput
        {
            DocumentTypeId = documentTypeId,
            File = new RemoteStreamContent(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("per-type grant body")),
                fileName,
                "text/plain")
        };
    }

    // The claim shape ABP's permission pipeline reads: AbpClaimTypes.UserId plus a non-empty authentication type.
    // UserName is carried too, and only because UploadAsync stamps CurrentUser.UserName into the required
    // FileOrigin.UploadedByUserName; nothing about authorization reads it.
    private static ClaimsPrincipal Principal(Guid userId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(AbpClaimTypes.UserId, userId.ToString()),
                new Claim(AbpClaimTypes.UserName, "per-type-grant-test")
            },
            authenticationType: "IntegrationTest"));
    }
}
