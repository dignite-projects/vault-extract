using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Duplicates;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// The one place a <see cref="DocumentType"/> is mutated and saved, so that the consequences of a change are
/// attached to the change rather than to each call site (#651 §5).
/// <para>
/// Today there is exactly one such consequence: moving <see cref="DocumentType.DuplicateScope"/> has to enqueue
/// <c>DuplicateScopeReconciliationJob</c>, because <see cref="DocumentReviewReasons.DuplicateSuspected"/> is a
/// persisted bit rather than a computed view and both switch directions leave a stale half. That obligation was
/// first written out twice — in <c>DocumentTypeAppService.UpdateAsync</c> and again in the pack import — as the
/// same four steps: capture the old value, apply the update, compare, enqueue. Two copies of a four-step rule is
/// how the pack import came to be missing it in the first place, and a third save path would have had to
/// rediscover it. There is one copy now, and a new save path gets the behaviour by calling this.
/// </para>
/// <para>
/// Application layer rather than <c>DocumentTypeManager</c>: enqueueing needs
/// <c>DuplicateScopeReconciliationArgs</c>, which is an Application type, and the Domain layer may not reach up
/// for it.
/// </para>
/// </summary>
public class DocumentTypeUpdater : ITransientDependency
{
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentTypeUpdater(
        IDocumentTypeRepository documentTypeRepository,
        IBackgroundJobManager backgroundJobManager)
    {
        _documentTypeRepository = documentTypeRepository;
        _backgroundJobManager = backgroundJobManager;
    }

    /// <summary>
    /// Applies <paramref name="applyUpdate"/> to <paramref name="documentType"/>, persists it, and enqueues
    /// whatever the resulting change implies.
    /// <para>
    /// The update arrives as a callback rather than as a parameter list precisely so that the before/after
    /// comparison cannot be skipped: a caller cannot mutate the entity outside the window in which this method is
    /// watching it. Reconciliation is enqueued <b>only on an actual scope change</b> — every other edit to a
    /// type, a rename or a threshold tweak, would otherwise sweep the whole type for nothing, and the job's own
    /// "read the live setting" recheck would find nothing to do.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when the duplicate scope moved and reconciliation was enqueued.</returns>
    public virtual async Task<bool> UpdateAsync(DocumentType documentType, Action<DocumentType> applyUpdate)
    {
        Check.NotNull(documentType, nameof(documentType));
        Check.NotNull(applyUpdate, nameof(applyUpdate));

        var previousDuplicateScope = documentType.DuplicateScope;

        applyUpdate(documentType);

        await _documentTypeRepository.UpdateAsync(documentType, autoSave: true);

        if (documentType.DuplicateScope == previousDuplicateScope)
        {
            return false;
        }

        await _backgroundJobManager.EnqueueAsync(
            new DuplicateScopeReconciliationArgs
            {
                DocumentTypeId = documentType.Id,
                TenantId = documentType.TenantId
            });

        return true;
    }
}
