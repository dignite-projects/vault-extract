using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Duplicates;

/// <summary>
/// One document whose persisted <see cref="DocumentReviewReasons.DuplicateSuspected"/> bit disagrees with the
/// verdict a given <see cref="DuplicateDetectionScope"/> justifies, together with the bit it should carry.
/// Rows already correct never become a verdict — that is what keeps the reconciliation job's row loads down to
/// the actual difference.
/// </summary>
public sealed record DuplicateScopeVerdict(DuplicateReconciliationRow Row, bool Target);

/// <summary>
/// What switching a type to a given scope would do to its <b>live</b> documents (#651 §5): the real diff, counted
/// by running the reconciliation verdict over every page.
/// </summary>
public readonly record struct DuplicateScopeImpact(long WillFlagCount, long WillClearCount);

/// <summary>
/// The duplicate-scope verdict, in one place (#651 §5): given a page of the narrow reconciliation projection and
/// a scope, which of those documents would have their <see cref="DocumentReviewReasons.DuplicateSuspected"/> bit
/// changed, and to what.
/// <para>
/// Two callers, and they must agree: <c>DuplicateScopeReconciliationJob</c> applies the verdicts, and
/// <c>DocumentTypeAppService.GetDuplicateScopePreviewAsync</c> counts them for the pre-save preview. A preview
/// computed by a second, cheaper rule — "how many are flagged today" — is not a preview of anything: it does not
/// answer how many documents actually move, which is the number the admin is deciding on. The only way those two
/// answers cannot drift is for there to be one of them.
/// </para>
/// <para>
/// <b>Not</b> <see cref="DuplicateDetectionEvaluator"/>. That asks the per-document question with one indexed
/// query per document, which is right for the three write paths that each handle one document. Reconciliation
/// asks it for a whole type, where per-document queries would be one round trip per row; it uses a single
/// aggregate per page instead. Same verdict, different cost model, deliberately separate code.
/// </para>
/// </summary>
public class DuplicateScopeVerdictCalculator : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;

    public DuplicateScopeVerdictCalculator(IDocumentRepository documentRepository)
    {
        _documentRepository = documentRepository;
    }

    /// <summary>
    /// The verdicts for one page of the reconciliation projection under <paramref name="detectionScope"/>: one
    /// aggregate bounded by the page's own fingerprints, then the diff in memory. Returns only the rows whose bit
    /// must change.
    /// <para>
    /// <see cref="DuplicateReconciliationRow.DuplicateAllowed"/> rows are dropped before anything else — an
    /// operator decided, and neither caller may re-litigate that or forge one — which also keeps their
    /// fingerprints out of the aggregate's <c>IN</c> list.
    /// </para>
    /// <para>
    /// Recycle-bin rows are <b>included</b>: the job must correct them so that restoring one later lands on the
    /// right verdict. The preview filters them out of its counts afterwards, because what it reports is what
    /// returns to or leaves the review queue.
    /// </para>
    /// </summary>
    public virtual async Task<List<DuplicateScopeVerdict>> ComputeChangesAsync(
        Guid documentTypeId,
        DuplicateDetectionScope detectionScope,
        IReadOnlyList<DuplicateReconciliationRow> page,
        CancellationToken cancellationToken = default)
    {
        var candidates = page.Where(r => !r.DuplicateAllowed).ToList();
        if (candidates.Count == 0)
        {
            return new List<DuplicateScopeVerdict>();
        }

        // Distinct, because a page routinely holds several documents of one bucket — which is the interesting
        // case rather than an edge one: they are duplicates of each other.
        var pageFingerprints = candidates
            .Select(r => r.FieldFingerprint)
            .Where(f => f != null)
            .Select(f => f!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var collisions = await _documentRepository.CountDuplicateCollisionsAsync(
            documentTypeId, detectionScope, pageFingerprints, cancellationToken);

        var changes = new List<DuplicateScopeVerdict>();
        foreach (var row in candidates)
        {
            var target = ResolveTargetFlag(row, detectionScope, collisions);
            var current = (row.ReviewReasons & DocumentReviewReasons.DuplicateSuspected)
                          != DocumentReviewReasons.None;

            if (current != target)
            {
                changes.Add(new DuplicateScopeVerdict(row, target));
            }
        }

        return changes;
    }

    /// <summary>
    /// The pre-save preview (#651 §5): how many <b>live</b> documents of this type would have the blocking
    /// duplicate reason <b>set</b>, and how many would have it <b>cleared</b>, if the type were switched to
    /// <paramref name="prospectiveScope"/>. Exactly the verdicts
    /// <c>DuplicateScopeReconciliationJob</c> would then apply, counted instead of written — so the number the
    /// admin is shown and the number the job changes are the same number by construction.
    /// <para>
    /// Both directions re-evaluate <b>every</b> fingerprinted document, so both can flag as well as clear. A
    /// narrowing switch clearing false parks is the headline case, but it can also newly flag: the stored bit is
    /// a snapshot taken at each document's own extraction, and the first upload of a key was written before its
    /// twin existed, so it sits unflagged while its twin carries the flag. Re-evaluation gives the first one the
    /// verdict it would have today.
    /// </para>
    /// <para>
    /// <b>No writes and no row loads</b> — only the narrow projection and one bounded aggregate per page. It does
    /// page the whole type in-process, though, so its cost grows with the type while an admin waits on it; that
    /// is the price of quoting the real diff rather than a heuristic, and it is the same total work the job then
    /// does asynchronously.
    /// </para>
    /// </summary>
    public virtual async Task<DuplicateScopeImpact> PreviewAsync(
        Guid documentTypeId,
        DuplicateDetectionScope prospectiveScope,
        CancellationToken cancellationToken = default)
    {
        var batchSize = DocumentConsts.ReprocessingDispatchBatchSize;
        Guid? afterId = null;
        long willFlag = 0;
        long willClear = 0;

        while (true)
        {
            var page = await _documentRepository.GetDuplicateReconciliationPageAsync(
                documentTypeId, afterId, batchSize, cancellationToken);

            if (page.Count == 0)
            {
                break;
            }

            foreach (var change in await ComputeChangesAsync(
                         documentTypeId, prospectiveScope, page, cancellationToken))
            {
                // Recycle-bin documents are reconciled but are not in anyone's review queue, so counting them
                // would overstate what the admin is about to see happen.
                if (change.Row.IsDeleted)
                {
                    continue;
                }

                if (change.Target)
                {
                    willFlag++;
                }
                else
                {
                    willClear++;
                }
            }

            if (page.Count < batchSize)
            {
                break;
            }

            afterId = page[^1].Id;
        }

        return new DuplicateScopeImpact(willFlag, willClear);
    }

    /// <summary>
    /// The verdict this row's scope justifies: does any <b>other live</b> document of the type share its
    /// collision key?
    /// <para>
    /// The row's own bucket includes it when it is live and excludes it when it is in the recycle bin, so the
    /// self-exclusion is conditional — exactly the asymmetry <c>FindDuplicateCandidatesAsync</c> produces for the
    /// same two cases (it excludes the subject by id, and excludes soft-deleted candidates through the ambient
    /// filter).
    /// </para>
    /// </summary>
    protected virtual bool ResolveTargetFlag(
        DuplicateReconciliationRow row,
        DuplicateDetectionScope detectionScope,
        IReadOnlyDictionary<DuplicateCollisionKey, int> collisions)
    {
        if (row.FieldFingerprint is not { } fingerprint)
        {
            // No unique key, or a partial one: there is nothing to compare, so the flag can never be justified.
            return false;
        }

        // Under Layer the uploader is not part of the key, and the repository grouped with a null anchor — build
        // the lookup key the same way or every bucket misses.
        var key = new DuplicateCollisionKey(
            fingerprint,
            detectionScope == DuplicateDetectionScope.Uploader ? row.CreatorId : null);

        collisions.TryGetValue(key, out var bucketSize);
        return bucketSize - (row.IsDeleted ? 0 : 1) > 0;
    }
}
