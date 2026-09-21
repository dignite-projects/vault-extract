using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Vault.Extract.Documents;

public class EfCoreDocumentRepository
    : EfCoreRepository<VaultExtractDbContext, Document, Guid>, IDocumentRepository
{
    /// <summary>
    /// Page size of the value-bag probe in <see cref="AnyFlexFieldValueAsync"/>. Each row carries a whole
    /// serialized bag, so this trades round trips against how much JSON is materialized at once.
    /// </summary>
    protected const int FlexFieldValueProbePageSize = 200;

    public EfCoreDocumentRepository(
        IDbContextProvider<VaultExtractDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        // Scoped to non-derived rows (OriginDocumentId == null): a derived sub-document has no FileOrigin (null),
        // so it can never match by BlobName anyway, but the explicit scoping keeps intent clear.
        return await dbSet
            .FirstOrDefaultAsync(
                d => d.FileOrigin != null && d.FileOrigin.BlobName == blobName && d.OriginDocumentId == null,
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task<Document?> FindByContentHashAsync(
        string contentHash,
        Guid? creatorId,
        CancellationToken cancellationToken = default)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            // #655: equality on the anchor like any other value — a null creatorId matches only a null CreatorId,
            // the same rule FindDuplicateCandidatesAsync applies under DuplicateDetectionScope.Uploader (#651).
            // Hoisted into a local for the same reason as there: one plain `d.CreatorId == owner` that the provider
            // translates to `IS NULL` when owner is null, which EfCoreDocumentRepositoryContentHash_Tests pins
            // against the real provider rather than assuming.
            var owner = creatorId;
            return await dbSet
                .FirstOrDefaultAsync(
                    d => d.FileOrigin != null
                        && d.FileOrigin.ContentHash == contentHash
                        && d.CreatorId == owner,
                    GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<int> UnassignCabinetDocumentsAsync(
        Guid cabinetId,
        CancellationToken cancellationToken = default)
    {
        // #530: clear live + recycle-bin references without loading an unbounded set of aggregate payloads. Only
        // ISoftDelete is disabled; ABP's ambient IMultiTenant predicate remains part of the generated UPDATE.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await dbSet
                .Where(document => document.CabinetId == cabinetId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(document => document.CabinetId, (Guid?)null),
                    GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<bool> AnyLiveDerivedDuplicateAsync(
        Guid originDocumentId,
        string originConstituentKey,
        Guid excludeDocumentId,
        CancellationToken cancellationToken = default)
    {
        // #485: the caller (DocumentAppService.RestoreAsync) runs inside DataFilter.Disable<ISoftDelete>() to load
        // the soft-deleted row being restored, so that ambient state reaches this query too -- a soft-deleted
        // sibling must NOT count as a live duplicate here, so filter explicitly on d.IsDeleted rather than relying
        // on the (here, disabled) global ISoftDelete filter. IMultiTenant still applies by ambient state.
        var dbSet = await GetDbSetAsync();
        return await dbSet.AnyAsync(
            d => d.OriginDocumentId == originDocumentId
                && d.OriginConstituentKey == originConstituentKey
                && d.Id != excludeDocumentId
                && !d.IsDeleted,
            GetCancellationToken(cancellationToken));
    }

    public virtual async Task<bool> AnyByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default)
    {
        // #508 delete guards: does this source still have derived sub-documents? Both global filters are left at
        // their AMBIENT state on purpose -- that is the whole contract of this method. IMultiTenant keeps the check
        // inside the source's own layer, and ISoftDelete decides whether recycle-bin children count: DeleteAsync
        // calls this with the filter on (live children only), PermanentDeleteAsync from inside its
        // DataFilter.Disable<ISoftDelete>() scope (recycle-bin children count too, since hard-deleting the source
        // reclaims the blob they reach through OriginDocumentId). Index-served by the plain OriginDocumentId index.
        var dbSet = await GetDbSetAsync();
        return await dbSet.AnyAsync(
            d => d.OriginDocumentId == originDocumentId,
            GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<DuplicateCandidateModel>> FindDuplicateCandidatesAsync(
        Guid documentId,
        Guid documentTypeId,
        string fieldFingerprint,
        int maxResults,
        DuplicateDetectionScope detectionScope,
        Guid? subjectCreatorId,
        DocumentAccessScope readScope,
        CancellationToken cancellationToken = default)
    {
        // #411: other documents in the current layer sharing this (type, fingerprint). The default IMultiTenant +
        // ISoftDelete global filters are intentionally NOT disabled, so the result stays within the document's own
        // layer and excludes recycle-bin documents. Equality on the indexed FieldFingerprint column; AsNoTracking +
        // a scalar projection (Id/Title/file name/upload time, no Markdown) keeps it light; Take is the fail-closed
        // cap on a widely-shared fingerprint.
        var dbSet = await GetDbSetAsync();
        IQueryable<Document> query = dbSet
            .AsNoTracking()
            .Where(d => d.DocumentTypeId == documentTypeId
                     && d.FieldFingerprint == fieldFingerprint
                     && d.Id != documentId);

        // #651: the type's detection scope decides WHAT COUNTS as a duplicate, and is applied first — otherwise
        // the operator panel would list candidates that never triggered the flag. Layer adds no predicate, which
        // is the pre-#651 behaviour every type keeps by default. Uploader compares the ownership anchor by plain
        // equality, so a null subject matches only null candidates; a residual predicate over a set the index has
        // already narrowed to (TenantId, DocumentTypeId, FieldFingerprint) and that Take caps at 20.
        //
        // Hoisted into a local because `subjectCreatorId` is a Nullable<Guid> parameter: closing over the
        // parameter itself is fine for EF, but the local keeps the comparison one plain `d.CreatorId == owner`
        // that the provider translates to `IS NULL` when owner is null, which is the semantics the acceptance
        // criteria pin against the real provider rather than assume.
        if (detectionScope == DuplicateDetectionScope.Uploader)
        {
            var owner = subjectCreatorId;
            query = query.Where(d => d.CreatorId == owner);
        }

        // #635: each candidate is named by title and file name, so the caller's read scope narrows them the same
        // way it narrows the list — orthogonal to the detection scope above. Unrestricted adds no predicate,
        // which is what the pipeline's own call gets.
        if (!readScope.IsUnrestricted)
        {
            query = query.Where(readScope.ToPredicate());
        }

        return await query
            .OrderBy(d => d.CreationTime)
            .Select(d => new DuplicateCandidateModel
            {
                Id = d.Id,
                Title = d.Title,
                FileName = d.FileOrigin != null ? d.FileOrigin.OriginalFileName : null,
                CreationTime = d.CreationTime
            })
            .Take(maxResults)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task<IQueryable<Document>> WithDetailsAsync()
    {
        return (await GetQueryableAsync()).IncludeDetails();
    }

    public virtual async Task<Document?> FindWithFieldValuesAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        // #527: load the field-stage child collection — FieldValidationWarnings — because the field-extraction
        // write phase and the §7 type-change clearing reconcile / delete it. The generic list path
        // (IncludeDetails) deliberately does NOT co-load warnings — it projects a bounded summary — so this is
        // the only path that does.
        var query = await WithDetailsAsync(d => d.FieldValidationWarnings);
        return await query
            .FirstOrDefaultAsync(d => d.Id == id, GetCancellationToken(cancellationToken));
    }

    public virtual async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Traverse only soft delete to physically delete already-soft-deleted rows, while preserving the IMultiTenant tenant boundary.
        // Never use IgnoreQueryFilters(), because it would also disable IMultiTenant and allow future callers without app-layer tenant validation
        // to hard-delete across tenants (#220).
        // ExecuteDeleteAsync relies on DB-level ON DELETE CASCADE. All three child FKs — DocumentFieldValidationWarning
        // (#527), DocumentPipelineRun, and DocumentSegment (#346/#371) — use OnDelete(Cascade), and the narrowed
        // filter does not affect cascading. DocumentFlexFieldIndex (#558) also cascades.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbContext = await GetDbContextAsync();
            await dbContext.Set<Document>()
                .Where(d => d.Id == id)
                .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<bool> AnyFlexFieldValueAsync(
        Field field,
        bool isIndexable,
        CancellationToken cancellationToken = default)
    {
        var token = GetCancellationToken(cancellationToken);
        var dbContext = await GetDbContextAsync();

        // A soft-deleted document keeps its values and gets them back on restore, so it counts — the same
        // conservative rule AnyExtractedFieldValueAsync got for free from scanning a child DbSet that never
        // had the parent's filter applied. IMultiTenant stays on either way.
        using (DataFilter.Disable<ISoftDelete>())
        {
            if (isIndexable && field.IsSearchable)
            {
                // Every value of an indexable, searchable field has an index row, so the index is exact here
                // and this is one indexed lookup rather than a scan.
                return await dbContext.Set<DocumentFlexFieldIndex>()
                    .AnyAsync(i => i.FieldId == field.Id, token);
            }

            // The index is legitimately empty for this field whatever its values are, so it cannot answer.
            // Page the type's documents and test the bag key in memory, stopping at the first hit. Only the
            // "no values" answer pays for the whole type; that is the answer that permits the change, and
            // this runs on an interactive admin edit rather than any pipeline path.
            var dbSet = await GetDbSetAsync();
            Guid? afterId = null;
            while (true)
            {
                var page = await dbSet
                    .AsNoTracking()
                    .Where(d => d.DocumentTypeId == field.DocumentTypeId)
                    .Where(d => afterId == null || d.Id.CompareTo(afterId.Value) > 0)
                    .OrderBy(d => d.Id)
                    .Take(FlexFieldValueProbePageSize)
                    .Select(d => new { d.Id, d.FlexFields })
                    .ToListAsync(token);

                if (page.Count == 0)
                {
                    return false;
                }

                if (page.Any(d => d.FlexFields.ContainsKey(field.Name)))
                {
                    return true;
                }

                afterId = page[^1].Id;
            }
        }
    }

    public virtual async Task<List<Guid>> GetIdsWithFieldValidationWarningAsync(
        Guid fieldDefinitionId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // #528: recycle-bin documents must be cleaned too — restoring one would otherwise resurrect a blocking
        // warning for a field that no longer exists. IMultiTenant stays on, so the scan never leaves this layer.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await ApplyKeysetPage(
                    dbSet.Where(d => d.FieldValidationWarnings.Any(w => w.FieldDefinitionId == fieldDefinitionId)),
                    afterId,
                    maxCount)
                .ToListAsync(GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<List<Guid>> GetIdsWithDuplicateBasisAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await ApplyKeysetPage(
                    dbSet.Where(d =>
                        d.DocumentTypeId == documentTypeId &&
                        (d.FieldFingerprint != null ||
                         (d.ReviewReasons & DocumentReviewReasons.DuplicateSuspected) != DocumentReviewReasons.None)),
                    afterId,
                    maxCount)
                .ToListAsync(GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<List<Guid>> GetIdsByDocumentTypeAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // Recycle-bin documents are in scope: restoring one after a rename must not bring back values under
        // the pre-rename key. IMultiTenant stays on, so the scan never leaves this layer.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await ApplyKeysetPage(
                    dbSet.Where(d => d.DocumentTypeId == documentTypeId),
                    afterId,
                    maxCount)
                .ToListAsync(GetCancellationToken(cancellationToken));
        }
    }

    /// <summary>
    /// Shared keyset page for the #528 cleanup scans: <c>WHERE Id &gt; afterId ORDER BY Id Take(N)</c>, riding the
    /// primary-key index (O(batch), unlike deep OFFSET paging), projecting Ids only so no full row — especially
    /// Markdown — is ever materialized.
    /// </summary>
    private static IQueryable<Guid> ApplyKeysetPage(IQueryable<Document> query, Guid? afterId, int maxCount)
        => KeysetPage(query, afterId, maxCount).Select(d => d.Id);

    /// <summary>
    /// The cursor half of <see cref="ApplyKeysetPage"/>, left un-projected so #651's reconciliation scan can
    /// select its own narrow shape instead of Ids. Split out rather than copied: two keyset cursors side by side
    /// is how one of them quietly stops advancing.
    /// </summary>
    private static IQueryable<Document> KeysetPage(IQueryable<Document> query, Guid? afterId, int maxCount)
    {
        if (afterId.HasValue)
        {
            var cursor = afterId.Value;
            query = query.Where(d => d.Id.CompareTo(cursor) > 0);
        }

        return query
            .OrderBy(d => d.Id)
            .Take(maxCount)
            .AsNoTracking();
    }

    public virtual async Task<Dictionary<DuplicateCollisionKey, int>> CountDuplicateCollisionsAsync(
        Guid documentTypeId,
        DuplicateDetectionScope detectionScope,
        IReadOnlyCollection<string> fingerprints,
        CancellationToken cancellationToken = default)
    {
        // #651 §5 step 1. Both global filters stay at their default ON state: IMultiTenant keeps the aggregate in
        // this type's own layer, and ISoftDelete excludes recycle-bin rows so a bucket counts exactly the LIVE
        // documents a collision could be against — the same population FindDuplicateCandidatesAsync sees. No
        // HAVING COUNT(*) > 1: a recycle-bin row is absent from its own bucket, so a bucket of size 1 still means
        // "that deleted document would collide on restore", and dropping singletons would leave every such row
        // stale. Served by the (TenantId, DocumentTypeId, FieldFingerprint) index; no row is materialized.
        Check.NotNull(fingerprints, nameof(fingerprints));

        // A page with no fingerprinted document at all asks about nothing, so there is nothing to ask the
        // database. Also the guard that keeps an empty IN list from reaching the provider.
        if (fingerprints.Count == 0)
        {
            return new Dictionary<DuplicateCollisionKey, int>();
        }

        var token = GetCancellationToken(cancellationToken);
        var dbSet = await GetDbSetAsync();

        // The fingerprint restriction is what bounds this aggregate: the caller names one page's worth of keys,
        // so the grouped result is O(batch), not O(the type's distinct fingerprints). Enumerable.Contains over an
        // IReadOnlyCollection<string> is the shape the relational provider already translates (the same one
        // DocumentAccessScope.ToPredicate relies on), and it rides the same index prefix as the grouping.
        var scoped = dbSet
            .AsNoTracking()
            .Where(d => d.DocumentTypeId == documentTypeId
                     && d.FieldFingerprint != null
                     && fingerprints.Contains(d.FieldFingerprint!));

        if (detectionScope == DuplicateDetectionScope.Uploader)
        {
            var byUploader = await scoped
                .GroupBy(d => new { d.FieldFingerprint, d.CreatorId })
                .Select(g => new { g.Key.FieldFingerprint, g.Key.CreatorId, Count = g.Count() })
                .ToListAsync(token);

            // The null-CreatorId group is a real bucket, not an absent one: every uploaderless document of the
            // type shares it, which is exactly what "null matches only null" means on the read side.
            return byUploader.ToDictionary(
                g => new DuplicateCollisionKey(g.FieldFingerprint!, g.CreatorId),
                g => g.Count);
        }

        var byFingerprint = await scoped
            .GroupBy(d => d.FieldFingerprint)
            .Select(g => new { FieldFingerprint = g.Key, Count = g.Count() })
            .ToListAsync(token);

        // Under Layer the uploader is not part of the key, so every bucket is keyed with a null anchor and the
        // job builds its lookup key the same way — one dictionary shape for both scopes.
        return byFingerprint.ToDictionary(
            g => new DuplicateCollisionKey(g.FieldFingerprint!, null),
            g => g.Count);
    }

    public virtual async Task<List<DuplicateReconciliationRow>> GetDuplicateReconciliationPageAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // #651 §5 step 2. Recycle-bin documents are IN scope here — the opposite of CountDuplicateCollisionsAsync
        // above — because restoring one must not bring back a flag derived from a rule the admin has since
        // retracted. IMultiTenant stays on. Six narrow columns, no Markdown, no aggregate payload.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await KeysetPage(
                    dbSet.Where(d => d.DocumentTypeId == documentTypeId),
                    afterId,
                    maxCount)
                .Select(d => new DuplicateReconciliationRow
                {
                    Id = d.Id,
                    FieldFingerprint = d.FieldFingerprint,
                    CreatorId = d.CreatorId,
                    ReviewReasons = d.ReviewReasons,
                    DuplicateAllowed = d.DuplicateAllowed,
                    IsDeleted = d.IsDeleted
                })
                .ToListAsync(GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<long> CountForReprocessingAsync(
        Guid? documentTypeId,
        DocumentReviewReasons? withReason,
        bool excludeManuallyConfirmed,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await ApplyReprocessingScope(dbSet, documentTypeId, withReason, excludeManuallyConfirmed)
            .LongCountAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<Guid>> GetIdsForReprocessingAsync(
        Guid? documentTypeId,
        DocumentReviewReasons? withReason,
        bool excludeManuallyConfirmed,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var query = ApplyReprocessingScope(dbSet, documentTypeId, withReason, excludeManuallyConfirmed);

        // Keyset cursor: WHERE Id > afterId ORDER BY Id Take(N). Uses the primary-key index and is O(batch), better than deep OFFSET pagination.
        if (afterId.HasValue)
        {
            var cursor = afterId.Value;
            query = query.Where(d => d.Id.CompareTo(cursor) > 0);
        }

        return await query
            .OrderBy(d => d.Id)
            .Take(maxCount)
            .AsNoTracking()
            .Select(d => d.Id)   // Never read full rows, especially Markdown, to avoid OOM.
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<DocumentStatisticsModel> GetStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();

        // A single GROUP BY pass yields per-status document counts, per-status byte sums, and the needs-review
        // tally folded in as a conditional sum — one DB round-trip. The ambient IMultiTenant + ISoftDelete global
        // filters keep this to the current layer's non-deleted documents (no filter disabling, no hand-written
        // tenant predicate). FileOrigin is an owned value object mapped to the same table, so Sum(FileOrigin.FileSize)
        // translates to a plain SQL SUM.
        //
        // The needs-review condition mirrors the canonical DocumentReviewQueries.RequiresAttention
        // (ReviewReasons != None && ReviewDisposition != Rejected, shared with DocumentAppService.ApplyFilter). It is
        // inlined here rather than reusing that Expression because EF Core cannot fold a shared Expression into a
        // grouped projection; keep the two in sync.
        var byStatus = await dbSet
            .GroupBy(d => d.LifecycleStatus)
            .Select(g => new {
                Status = g.Key,
                // #346: a container is an infrastructure wrapper, not a business document — its sub-documents are the
                // real records. Exclude containers from the document counts / storage so a container + its N
                // sub-documents do not double-count. But a segmentation-incomplete container DOES need operator
                // attention, so it is INCLUDED in NeedsReview below — the review-queue list (DocumentAppService.ApplyFilter)
                // counts it too, so the dashboard count and the queue never drift (#333).
                // The byte sum ALSO excludes every derived sub-document (OriginDocumentId != null) — a derived
                // document has no FileOrigin of its own (null), so it owns no distinct bytes to sum. Document
                // COUNTS are unaffected by this exclusion — a derived sub-document is still a real document.
                // FileOrigin is optional (owned reference navigation): the `d.FileOrigin == null` guard covers a
                // non-derived non-container row defensively, even though a normal upload always has one.
                Count = g.Sum(d => d.IsContainer ? 0L : 1L),
                Bytes = g.Sum(d => d.IsContainer || d.OriginDocumentId != null || d.FileOrigin == null ? 0L : d.FileOrigin.FileSize),
                NeedsReview = g.Sum(d =>
                    d.ReviewReasons != DocumentReviewReasons.None
                    && d.ReviewDisposition != DocumentReviewDisposition.Rejected
                        ? 1L
                        : 0L)
            })
            .ToListAsync(GetCancellationToken(cancellationToken));

        long CountOf(DocumentLifecycleStatus status)
            => byStatus.FirstOrDefault(b => b.Status == status)?.Count ?? 0;

        return new DocumentStatisticsModel
        {
            TotalCount = byStatus.Sum(b => b.Count),
            UploadedCount = CountOf(DocumentLifecycleStatus.Uploaded),
            ProcessingCount = CountOf(DocumentLifecycleStatus.Processing),
            PendingReviewCount = CountOf(DocumentLifecycleStatus.PendingReview),
            ReadyCount = CountOf(DocumentLifecycleStatus.Ready),
            FailedCount = CountOf(DocumentLifecycleStatus.Failed),
            NeedsReviewCount = byStatus.Sum(b => b.NeedsReview),
            TotalStorageBytes = byStatus.Sum(b => b.Bytes)
        };
    }

    /// <summary>
    /// Shared scope predicate for bulk reprocessing (#289). <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters are applied
    /// automatically by ambient state, so trash / cross-tenant documents are out of scope. Always requires completed text extraction
    /// (<c>Markdown</c> non-empty) because reclassification / field extraction both need text payload and never-extracted documents cannot be reprocessed.
    /// See <see cref="IDocumentRepository.CountForReprocessingAsync"/> for the remaining conditions.
    /// </summary>
    private static IQueryable<Document> ApplyReprocessingScope(
        IQueryable<Document> query,
        Guid? documentTypeId,
        DocumentReviewReasons? withReason,
        bool excludeManuallyConfirmed)
    {
        query = query.Where(d => d.Markdown != null && d.Markdown != "");

        if (documentTypeId.HasValue)
        {
            var typeId = documentTypeId.Value;
            query = query.Where(d => d.DocumentTypeId == typeId);
        }

        if (withReason.HasValue && withReason.Value != DocumentReviewReasons.None)
        {
            // #284 two-axis model: review reasons are a [Flags] bitset. "Contains this reason" = bitwise AND non-zero.
            var reason = withReason.Value;
            query = query.Where(d => (d.ReviewReasons & reason) != DocumentReviewReasons.None);
        }

        if (excludeManuallyConfirmed)
        {
            // Protect manual confirmation: exclude documents with operator-confirmed (Confirmed disposition).
            query = query.Where(d => d.ReviewDisposition != DocumentReviewDisposition.Confirmed);
        }

        return query;
    }

}
