using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Abstractions.Documents;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Documents.Fields.Cleanup;
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
using Xunit;

namespace Dignite.Vault.Extract.EntityFrameworkCore.Documents;

[DependsOn(typeof(VaultExtractEntityFrameworkCoreTestModule))]
public class FieldDefinitionCleanupTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // IBackgroundJobManager is substituted for two reasons: to assert what DeleteAsync enqueues, and to stop the
        // jobs' self-chaining from actually running a second batch inside a test.
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<VaultExtractDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// Integration tests for the #528 field-definition delete reconciliation, against the real SQLite DB so the cleanup
/// scans meet ABP's ambient <c>ISoftDelete</c> / <c>IMultiTenant</c> filters and the warning rows are really deleted
/// rather than just detached in memory.
/// <para>
/// The scope line under test is the Ready gate: state that <b>withholds</b> a document is reconciled on delete, state
/// that is merely stale is not (#537). So these tests pin two things — that an orphaned warning stops parking the
/// document, and that the duplicate basis is dropped only in the one case where it becomes a false park (the type's
/// last unique-key field going away).
/// </para>
/// </summary>
public class FieldDefinitionCleanup_Tests : VaultExtractTestBase<FieldDefinitionCleanupTestModule>
{
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;
    private readonly FieldValidationWarningCleanupJob _warningCleanupJob;
    private readonly DuplicateBasisCleanupJob _duplicateBasisCleanupJob;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IDataFilter _dataFilter;

    public FieldDefinitionCleanup_Tests()
    {
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
        _warningCleanupJob = GetRequiredService<FieldValidationWarningCleanupJob>();
        _duplicateBasisCleanupJob = GetRequiredService<DuplicateBasisCleanupJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    // === FieldDefinitionAppService.DeleteAsync wiring ===

    [Fact]
    public async Task DeleteAsync_Enqueues_The_Warning_Cleanup_For_The_Deleted_Field()
    {
        var typeId = await ArrangeTypeAsync();
        var fieldId = await ArrangeFieldAsync(typeId, "party_a_name");

        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(fieldId));

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<FieldValidationWarningCleanupArgs>(a => a.FieldDefinitionId == fieldId && a.AfterId == null),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DeleteAsync_Enqueues_Duplicate_Basis_Cleanup_For_A_Unique_Key_Field()
    {
        // The one case where deletion invalidates the duplicate basis outright: no unique-key field remains, so the
        // type has nothing to fingerprint and any surviving DuplicateSuspected is a blocking false park.
        var typeId = await ArrangeTypeAsync();
        var uniqueKeyFieldId = await ArrangeFieldAsync(typeId, "invoice_number", isUniqueKey: true);
        await ArrangeFieldAsync(typeId, "note");

        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(uniqueKeyFieldId));

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DuplicateBasisCleanupArgs>(a => a.DocumentTypeId == typeId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DeleteAsync_Still_Enqueues_Duplicate_Basis_Check_While_Another_Unique_Key_Field_Remains()
    {
        // The job, rather than the request's pre-delete snapshot, decides whether this was the last key. Always
        // enqueueing closes the concurrent-final-two-deletes race; when this other key survives, the job is a no-op.
        var typeId = await ArrangeTypeAsync();
        var firstKeyFieldId = await ArrangeFieldAsync(typeId, "invoice_number", isUniqueKey: true);
        await ArrangeFieldAsync(typeId, "vendor", isUniqueKey: true);

        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(firstKeyFieldId));

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DuplicateBasisCleanupArgs>(a => a.DocumentTypeId == typeId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task DeleteAsync_Does_Not_Enqueue_Duplicate_Basis_Cleanup_For_A_Plain_Field()
    {
        var typeId = await ArrangeTypeAsync();
        var plainFieldId = await ArrangeFieldAsync(typeId, "note");
        await ArrangeFieldAsync(typeId, "invoice_number", isUniqueKey: true);

        await WithUnitOfWorkAsync(() => _fieldDefinitionAppService.DeleteAsync(plainFieldId));

        await _backgroundJobManager.DidNotReceive().EnqueueAsync(
            Arg.Any<DuplicateBasisCleanupArgs>(),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    // === FieldValidationWarningCleanupJob ===

    [Fact]
    public async Task Cleanup_Removes_The_Orphaned_Warning_And_Unblocks_The_Document()
    {
        // #528 itself: the warning row survives the field-definition delete, and with it the blocking
        // FieldValidationWarning bit, so the document stays withheld from DocumentReadyEto with nothing having
        // happened to it.
        var typeId = await ArrangeTypeAsync();
        var fieldId = await ArrangeFieldAsync(typeId, "party_a_name");
        var documentId = await ArrangeDocumentWithWarningsAsync(typeId, (fieldId, "Value looks truncated."));

        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(fieldId));

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            document!.FieldValidationWarnings.ShouldBeEmpty();
            HasWarningReason(document).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task Cleanup_Keeps_Warnings_For_Other_Fields_And_Leaves_The_Document_Blocked()
    {
        // The collection and the bit stay coupled through the aggregate: only the last warning clears the bit, so a
        // document still warned on another field must remain parked.
        var typeId = await ArrangeTypeAsync();
        var deletedFieldId = await ArrangeFieldAsync(typeId, "party_a_name");
        var survivingFieldId = await ArrangeFieldAsync(typeId, "amount");
        var documentId = await ArrangeDocumentWithWarningsAsync(
            typeId,
            (deletedFieldId, "Value looks truncated."),
            (survivingFieldId, "Not a number."));

        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(deletedFieldId));

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            document!.FieldValidationWarnings.Count.ShouldBe(1);
            document.FieldValidationWarnings.ShouldContain(w => w.FieldDefinitionId == survivingFieldId);
            HasWarningReason(document).ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Cleanup_Also_Cleans_Recycle_Bin_Documents_Without_Resurrecting_Them()
    {
        // Required by #528: otherwise restoring the document later resurrects review state for a field that no
        // longer exists. The scan therefore traverses soft delete — and the write must not undo the deletion.
        var typeId = await ArrangeTypeAsync();
        var fieldId = await ArrangeFieldAsync(typeId, "party_a_name");
        var documentId = await ArrangeDocumentWithWarningsAsync(typeId, (fieldId, "Value looks truncated."));
        await WithUnitOfWorkAsync(() => _documentRepository.DeleteAsync(documentId, autoSave: true));

        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(fieldId));

        await WithUnitOfWorkAsync(async () =>
        {
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
                document!.FieldValidationWarnings.ShouldBeEmpty();
                HasWarningReason(document).ShouldBeFalse();
                document.IsDeleted.ShouldBeTrue();
            }
        });
    }

    [Fact]
    public async Task Cleanup_Ignores_Documents_Warned_Only_On_Other_Fields()
    {
        var typeId = await ArrangeTypeAsync();
        var deletedFieldId = await ArrangeFieldAsync(typeId, "party_a_name");
        var otherFieldId = await ArrangeFieldAsync(typeId, "amount");
        var untouchedId = await ArrangeDocumentWithWarningsAsync(typeId, (otherFieldId, "Not a number."));

        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(deletedFieldId));

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(untouchedId);
            document!.FieldValidationWarnings.Count.ShouldBe(1);
            HasWarningReason(document).ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Cleanup_Is_Idempotent()
    {
        // At-least-once chaining can re-run a batch after a crash. Cleaned documents drop out of the scan predicate,
        // so the second run must be a no-op rather than a double-apply or a failure.
        var typeId = await ArrangeTypeAsync();
        var fieldId = await ArrangeFieldAsync(typeId, "party_a_name");
        var documentId = await ArrangeDocumentWithWarningsAsync(typeId, (fieldId, "Value looks truncated."));

        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(fieldId));
        await _warningCleanupJob.ExecuteAsync(NewWarningArgs(fieldId));

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            document!.FieldValidationWarnings.ShouldBeEmpty();
            HasWarningReason(document).ShouldBeFalse();
        });
    }

    // === DuplicateBasisCleanupJob ===

    [Fact]
    public async Task DuplicateBasisCleanup_Clears_The_Fingerprint_And_The_Blocking_Reason()
    {
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDuplicateSuspectedDocumentAsync(typeId);

        await _duplicateBasisCleanupJob.ExecuteAsync(new DuplicateBasisCleanupArgs
        {
            DocumentTypeId = typeId,
            TenantId = null
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            document!.FieldFingerprint.ShouldBeNull();
            ((document.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None)
                .ShouldBeFalse();
        });
    }

    [Fact]
    public async Task DuplicateBasisCleanup_Skips_When_The_Active_Schema_Has_A_Unique_Key()
    {
        // Covers both a surviving sibling key and a deleted last key restored/replaced before the queued job runs:
        // valid duplicate state must not be erased from a schema that still has a duplicate basis.
        var typeId = await ArrangeTypeAsync();
        await ArrangeFieldAsync(typeId, "invoice_number", isUniqueKey: true);
        var documentId = await ArrangeDuplicateSuspectedDocumentAsync(typeId);

        await _duplicateBasisCleanupJob.ExecuteAsync(new DuplicateBasisCleanupArgs
        {
            DocumentTypeId = typeId,
            TenantId = null
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.FindWithFieldValuesAsync(documentId);
            document!.FieldFingerprint.ShouldNotBeNull();
            ((document.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None)
                .ShouldBeTrue();
        });
    }

    [Fact]
    public async Task DuplicateBasisCleanup_Does_Not_Forge_An_Operator_Duplicate_Decision()
    {
        // Document.AllowDuplicate() would ALSO set DuplicateAllowed, durably recording a "not a duplicate" call that
        // no operator made and suppressing re-raises on every later re-extraction. Schema reconciliation clears the
        // reason only; a future re-extraction must be free to flag the document again on the remaining schema.
        var typeId = await ArrangeTypeAsync();
        var documentId = await ArrangeDuplicateSuspectedDocumentAsync(typeId);

        await _duplicateBasisCleanupJob.ExecuteAsync(new DuplicateBasisCleanupArgs
        {
            DocumentTypeId = typeId,
            TenantId = null
        });

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.FindWithFieldValuesAsync(documentId))!.DuplicateAllowed.ShouldBeFalse());
    }

    // === Arrangement ===

    private static FieldValidationWarningCleanupArgs NewWarningArgs(Guid fieldDefinitionId) =>
        new() { FieldDefinitionId = fieldDefinitionId, TenantId = null };

    private static bool HasWarningReason(Document document) =>
        (document.ReviewReasons & DocumentReviewReasons.FieldValidationWarning) != DocumentReviewReasons.None;

    private async Task<Guid> ArrangeTypeAsync(string typeCode = "invoice")
    {
        var typeId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() =>
            _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, tenantId: null, typeCode, "Invoice"), autoSave: true));
        return typeId;
    }

    private async Task<Guid> ArrangeFieldAsync(Guid documentTypeId, string name, bool isUniqueKey = false)
    {
        var fieldId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() =>
            _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(
                    fieldId,
                    tenantId: null,
                    documentTypeId,
                    name,
                    displayName: name,
                    prompt: null,
                    FieldDataType.Text,
                    isUniqueKey: isUniqueKey),
                autoSave: true));
        return fieldId;
    }

    private async Task<Guid> ArrangeDocumentWithWarningsAsync(
        Guid documentTypeId,
        params (Guid FieldId, string Message)[] warnings)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(documentId, tenantId: null, DocumentTestData.NewFileOrigin(documentId));
            DocumentTestData.MarkClassified(document, documentTypeId);

            // ReplaceFieldValidationWarnings couples the blocking bit itself, so seeding through the aggregate
            // reproduces exactly the state the field-extraction write phase leaves behind.
            var incoming = new List<FieldValidationWarning>();
            foreach (var (fieldId, message) in warnings)
            {
                incoming.Add(new FieldValidationWarning(fieldId, message));
            }

            document.ReplaceFieldValidationWarnings(incoming);
            await _documentRepository.InsertAsync(document, autoSave: true);
        });
        return documentId;
    }

    private async Task<Guid> ArrangeDuplicateSuspectedDocumentAsync(Guid documentTypeId)
    {
        var documentId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var document = new Document(documentId, tenantId: null, DocumentTestData.NewFileOrigin(documentId));
            DocumentTestData.MarkClassified(document, documentTypeId);
            document.SetFieldFingerprint(new string('a', 64));
            document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: true);
            await _documentRepository.InsertAsync(document, autoSave: true);
        });
        return documentId;
    }
}
