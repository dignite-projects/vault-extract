using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Data;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class DocumentTypeDeleteTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Restore publishes a lifecycle ETO and the wider DocumentAppService graph touches these out-of-process
        // collaborators; substitute them so the test exercises the real DB + the real guard queries.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the #531 document-type deletion guard and its restore-side twin, run against the real
/// SQLite DB so the in-use <c>EXISTS</c> query actually meets ABP's ambient <c>ISoftDelete</c> / <c>IMultiTenant</c>
/// global filters. These behaviours cannot be pinned against a mocked <c>IDocumentRepository</c> at all — a
/// substitute returns whatever it was told to regardless of the ambient filter state, so a mock-based test would stay
/// green with the fix removed.
/// <para>
/// The invariant: a document type is schema identity, not optional organization metadata, so deletion fails closed
/// while <b>any restorable</b> document still references it — recycle-bin documents included. That is the whole
/// difference from <c>CabinetAppService.DeleteAsync</c>, whose membership is optional and which therefore clears
/// references instead of blocking (see <c>CabinetDelete_Tests</c>). Only a permanently deleted document stops
/// counting, because it can never come back.
/// </para>
/// </summary>
public class DocumentTypeDelete_Tests : VaultExtractTestBase<DocumentTypeDeleteTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IDocumentAppService _documentAppService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDistributedEventBus _eventBus;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;

    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public DocumentTypeDelete_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _documentAppService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
    }

    // === DeleteAsync (the in-use guard) ===

    [Fact]
    public async Task DeleteAsync_Is_Blocked_While_A_Live_Document_References_The_Type()
    {
        var typeId = await ArrangeTypeAsync();
        await ArrangeDocumentAsync(typeId);

        var exception = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(typeId)));

        exception.Code.ShouldBe(VaultExtractErrorCodes.DocumentType.InUse);
        await AssertTypeIsLiveAsync(typeId);
    }

    [Fact]
    public async Task DeleteAsync_Is_Blocked_When_The_Referencing_Documents_Are_Only_In_The_Recycle_Bin()
    {
        // #531 itself. Before the fix the in-use EXISTS ran under the ambient ISoftDelete filter, so recycle-bin
        // documents did not count: an operator could bin every referencing document, delete the type (cascading its
        // field definitions away), then restore one — ending up with a live document whose schema identity points at
        // a deleted type. The UI could then only fall back to the raw type code, its fields were not editable, and
        // field re-extraction silently skipped it.
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDocumentAsync(typeId);
        await SoftDeleteDocumentAsync(documentId);

        var exception = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(typeId)));

        exception.Code.ShouldBe(VaultExtractErrorCodes.DocumentType.InUse);
        await AssertTypeIsLiveAsync(typeId);
    }

    [Fact]
    public async Task DeleteAsync_Succeeds_And_Cascades_To_Field_Definitions_When_No_Document_References_The_Type()
    {
        var typeId = await ArrangeTypeAsync();
        var fieldId = await ArrangeFieldAsync(typeId);

        await WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(typeId));

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentTypeRepository.FindAsync(typeId)).ShouldBeNull();
            (await _fieldDefinitionRepository.FindAsync(fieldId)).ShouldBeNull();

            using (_dataFilter.Disable<ISoftDelete>())
            {
                // Soft-deleted, not physically gone: RestoreAsync must still be able to find both rows.
                (await _documentTypeRepository.GetAsync(typeId)).IsDeleted.ShouldBeTrue();
                (await _fieldDefinitionRepository.GetAsync(fieldId)).IsDeleted.ShouldBeTrue();
            }
        });
    }

    [Fact]
    public async Task DeleteAsync_Ignores_A_Permanently_Deleted_Document()
    {
        // A hard-deleted row is physically gone, so it is not a restorable reference and correctly stops blocking.
        // This is the operator's escape hatch out of the guard: purge the document, then the type will go.
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDocumentAsync(typeId);
        await WithUnitOfWorkAsync(() => _documentAppService.PermanentDeleteAsync(documentId));

        await WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(typeId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentTypeRepository.FindAsync(typeId)).ShouldBeNull());
    }

    [Fact]
    public async Task DeleteAsync_Does_Not_Count_A_Document_From_Another_Layer()
    {
        // The risk the #531 change itself introduces: disabling ISoftDelete for the in-use check must NOT take
        // IMultiTenant down with it. Another layer's document is invisible here and must never block — or even be
        // read by — this layer's type deletion.
        var typeId = await ArrangeTypeAsync();
        await ArrangeDocumentAsync(typeId, tenantId: OtherTenantId);

        await WithUnitOfWorkAsync(() => _documentTypeAppService.DeleteAsync(typeId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentTypeRepository.FindAsync(typeId)).ShouldBeNull());
    }

    // === RestoreAsync (the defense-in-depth twin) ===

    [Fact]
    public async Task RestoreAsync_Is_Blocked_When_The_Referenced_Type_Was_Deleted()
    {
        // The guard above makes this state unreachable through the app service, so the corrupted state is seeded
        // out-of-band through the repository — which is exactly the population this check defends: legacy rows,
        // manual DB edits, and a delete/classification race.
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDocumentAsync(typeId);
        await SoftDeleteDocumentAsync(documentId);
        await SoftDeleteTypeOutOfBandAsync(typeId);

        var exception = await Should.ThrowAsync<BusinessException>(() =>
            WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentId)));

        exception.Code.ShouldBe(VaultExtractErrorCodes.Document.RestoreTypeDeleted);

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                // A rejected restore changes nothing: the document stays in the recycle bin.
                (await _documentRepository.GetAsync(documentId)).IsDeleted.ShouldBeTrue();
            }
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentRestoredEto>());
    }

    [Fact]
    public async Task RestoreAsync_Succeeds_Once_The_Type_Is_Restored_First()
    {
        // The documented remedy for the error above, and the reason it names the type code.
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDocumentAsync(typeId);
        await SoftDeleteDocumentAsync(documentId);
        await SoftDeleteTypeOutOfBandAsync(typeId);

        await WithUnitOfWorkAsync(() => _documentTypeAppService.RestoreAsync(typeId));
        await WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(documentId)).ShouldNotBeNull());

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<DocumentRestoredEto>(e => e.DocumentId == documentId));
    }

    [Fact]
    public async Task RestoreAsync_Skips_The_Type_Check_For_An_Unclassified_Document()
    {
        // DocumentTypeId is null until the classification stage runs, so the guard must not touch a document that
        // never had a type to begin with.
        var documentId = await ArrangeDocumentAsync(documentTypeId: null);
        await SoftDeleteDocumentAsync(documentId);

        await WithUnitOfWorkAsync(() => _documentAppService.RestoreAsync(documentId));

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindAsync(documentId)).ShouldNotBeNull());
    }

    // === Arrangement ===

    private async Task<Guid> ArrangeTypeAsync(string typeCode = "contract")
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() =>
            _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, typeCode, "Contract"),
                autoSave: true));
        return typeId;
    }

    private async Task<Guid> ArrangeFieldAsync(Guid documentTypeId)
    {
        var fieldId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() =>
            _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    fieldId,
                    tenantId: null,
                    documentTypeId,
                    name: "party_a_name",
                    displayName: "Party A",
                    prompt: null,
                    FieldDataType.Text),
                autoSave: true));
        return fieldId;
    }

    private async Task<Guid> ArrangeDocumentAsync(Guid? documentTypeId = null, Guid? tenantId = null)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            using (_currentTenant.Change(tenantId))
            {
                var document = new Document(documentId, tenantId, DocumentTestData.NewFileOrigin(documentId));
                if (documentTypeId.HasValue)
                {
                    DocumentTestData.MarkClassified(document, documentTypeId.Value);
                }

                await _documentRepository.InsertAsync(document, autoSave: true);
            }
        });
        return documentId;
    }

    private Task SoftDeleteDocumentAsync(Guid documentId) =>
        WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId, autoSave: true));

    /// <summary>
    /// Soft-deletes the type straight through the repository, bypassing <c>DeleteAsync</c>'s in-use guard. Since
    /// #531 that guard makes "a restorable document referencing a deleted type" unreachable through the app service,
    /// so the corrupted state the restore check exists for has to be seeded directly.
    /// </summary>
    private Task SoftDeleteTypeOutOfBandAsync(Guid typeId) =>
        WithUnitOfWorkAsync(() => _documentTypeRepository.DeleteAsync(typeId, autoSave: true));

    private Task AssertTypeIsLiveAsync(Guid typeId) =>
        WithUnitOfWorkAsync(async () =>
            (await _documentTypeRepository.FindAsync(typeId)).ShouldNotBeNull());
}
