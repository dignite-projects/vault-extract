using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Fields.Cleanup;

/// <summary>
/// Drops the duplicate basis left behind when a document type loses its <b>last active unique-key field</b> (#528).
/// <para>
/// <see cref="Document.FieldFingerprint"/> is the hash of the type's unique-key field values (#411), so once no
/// unique-key field remains the type has no duplicate basis at all: every fingerprint should be <c>null</c>, and any
/// lingering blocking <see cref="DocumentReviewReasons.DuplicateSuspected"/> is derived from a schema that no longer
/// exists — a false park identical in shape to the orphaned-warning bug this issue is named for. This mirrors what
/// <c>FieldExtractionService</c>'s no-field-definition branch already does for the whole-type case.
/// </para>
/// <para>
/// <b>Only the last-key case.</b> Deleting one of <i>several</i> unique-key fields merely <i>narrows</i> the key, and
/// two documents whose wide key values were equal necessarily have equal narrow key values — existing
/// <c>DuplicateSuspected</c> flags stay valid there, and clearing them would hide real duplicates. That case is an
/// under-detection of new collisions, not a park, and is tracked in #537. Every unique-key deletion enqueues this
/// job; the job rechecks the final active schema and only runs when no unique-key field remains. That execution-time
/// decision is resilient to concurrent deletes and to a field restore/new key before the queued work starts.
/// </para>
/// <para>
/// Note this clears the reason through <see cref="Document.SetReviewReason"/>, <b>not</b>
/// <see cref="Document.AllowDuplicate"/>: the latter also sets <c>DuplicateAllowed</c>, durably recording an operator
/// "not a duplicate" decision that nobody made and suppressing re-raises on every later re-extraction. Schema
/// reconciliation must not forge a human judgement.
/// </para>
/// <para>
/// Idempotent, retry-safe, and chained in bounded batches on the same terms as
/// <see cref="FieldValidationWarningCleanupJob"/>; publishes no <c>FieldsExtractedEto</c> because no field value is
/// re-extracted. A later re-extraction recomputes a fingerprint from the remaining schema, as it always has.
/// </para>
/// </summary>
[BackgroundJobName("VaultExtract.DuplicateBasisCleanup")]
public class DuplicateBasisCleanupJob
    : AsyncBackgroundJob<DuplicateBasisCleanupArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDataFilter _dataFilter;

    public DuplicateBasisCleanupJob(
        IDocumentRepository documentRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        DocumentPipelineRunManager pipelineRunManager,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant,
        IUnitOfWorkManager unitOfWorkManager,
        IDataFilter dataFilter)
    {
        _documentRepository = documentRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _pipelineRunManager = pipelineRunManager;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
        _unitOfWorkManager = unitOfWorkManager;
        _dataFilter = dataFilter;
    }

    public override async Task ExecuteAsync(DuplicateBasisCleanupArgs args)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;

        using (_currentTenant.Change(args.TenantId))
        {
            List<Guid> ids;

            using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
            {
                // Decide against the active schema at execution time, not against a snapshot taken before the
                // delete committed. Every unique-key deletion enqueues this job, which closes two races:
                // - concurrent deletion of the final two keys cannot result in neither request enqueueing cleanup;
                // - restoring/adding a key before cleanup cannot cause valid duplicate state to be erased.
                // Recheck on every chained batch so a key restored between batches stops further clearing.
                var activeDefinitions = await _fieldDefinitionRepository.GetListAsync(args.DocumentTypeId);
                if (activeDefinitions.Exists(field => field.IsUniqueKey))
                {
                    await uow.CompleteAsync();
                    Logger.LogInformation(
                        "Duplicate basis cleanup skipped for type {DocumentTypeId}: the active schema still has a unique-key field.",
                        args.DocumentTypeId);
                    return;
                }

                ids = await _documentRepository.GetIdsWithDuplicateBasisAsync(
                    args.DocumentTypeId, args.AfterId, batchSize);

                foreach (var id in ids)
                {
                    // Recycle-bin documents are in scope too: restoring one must not bring back a duplicate flag
                    // derived from a unique-key schema that no longer exists.
                    using (_dataFilter.Disable<ISoftDelete>())
                    {
                        var document = await _documentRepository.FindWithFieldValuesAsync(id);
                        if (document == null)
                        {
                            continue;
                        }

                        document.SetFieldFingerprint(null);
                        document.SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: false);

                        if (!document.IsDeleted)
                        {
                            await _pipelineRunManager.ReDeriveLifecycleAsync(document);
                        }

                        await _documentRepository.UpdateAsync(document);
                    }
                }

                if (ids.Count == batchSize)
                {
                    await _backgroundJobManager.EnqueueAsync(
                        new DuplicateBasisCleanupArgs
                        {
                            DocumentTypeId = args.DocumentTypeId,
                            TenantId = args.TenantId,
                            AfterId = ids[^1]
                        });
                }

                await uow.CompleteAsync();
            }

            Logger.LogInformation(
                "Duplicate basis cleanup: cleared {Count} document(s) for type {DocumentTypeId} after its last unique-key field was deleted (afterId={AfterId}, continued={Continued}).",
                ids.Count, args.DocumentTypeId, args.AfterId, ids.Count == batchSize);
        }
    }
}

public class DuplicateBasisCleanupArgs
{
    public Guid DocumentTypeId { get; set; }

    public Guid? TenantId { get; set; }

    /// <summary>Keyset cursor: only documents with <c>Id &gt; AfterId</c>; null for the first batch.</summary>
    public Guid? AfterId { get; set; }
}
