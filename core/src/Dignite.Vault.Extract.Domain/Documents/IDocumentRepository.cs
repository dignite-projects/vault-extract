using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents;

public interface IDocumentRepository : IRepository<Document, Guid>
{
    /// <summary>
    /// Finds a <b>non-derived</b> document by exact <c>FileOrigin.BlobName</c>. Scoped to
    /// <c>OriginDocumentId == null</c>. Does not traverse soft delete (unlike <see cref="FindByContentHashAsync"/>):
    /// only an active, non-recycle-bin document's own blob name resolves.
    /// </summary>
    Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a document by exact upload <c>FileOrigin.ContentHash</c> — the #221 upload-time dedup check
    /// (<c>DocumentAppService.UploadAsync</c>).
    /// <para>
    /// Traverses soft-delete (the implementation disables <c>ISoftDelete</c>): a re-upload matching a recycle-bin
    /// document's hash must still surface as <c>Document.InRecycleBin</c>, not silently be accepted as new.
    /// </para>
    /// </summary>
    Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <see cref="Document.CabinetId"/> for every document in the ambient tenant layer that references
    /// <paramref name="cabinetId"/> (#530). Traverses <c>ISoftDelete</c> so recycle-bin documents are unfiled too,
    /// while retaining the ambient <c>IMultiTenant</c> boundary. Implemented as one set-based update: cabinet
    /// membership is optional organization metadata, and its removal publishes no document pipeline events.
    /// Returns the number of affected rows and is idempotent/retry-safe.
    /// </summary>
    Task<int> UnassignCabinetDocumentsAsync(
        Guid cabinetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether another LIVE document already shares the derived-document identity
    /// <c>(OriginDocumentId, OriginConstituentKey)</c> — the #485 restore-time fail-close used by
    /// <c>DocumentAppService.RestoreAsync</c>, replacing the fail-close the #481-dropped #391 filtered-unique index
    /// used to give for free (a retracted, soft-deleted child and a later re-spawned successor may now freely
    /// share the same <c>OriginConstituentKey</c> per #481, but restoring the retracted one back to life while a
    /// live successor already occupies that identity would create a duplicate). Existence-only (SQL <c>EXISTS</c>,
    /// no row materialization).
    /// <para>
    /// Filters <c>OriginDocumentId == originDocumentId &amp;&amp; OriginConstituentKey == originConstituentKey
    /// &amp;&amp; Id != excludeDocumentId &amp;&amp; !IsDeleted</c>. This deliberately does <b>not</b> disable the
    /// <c>ISoftDelete</c> filter itself — soft-deleted siblings must
    /// NOT count here — but the caller (<c>RestoreAsync</c>) runs inside its own
    /// <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> scope to load the row being restored, so that ambient
    /// disabled state also reaches this call; the explicit <c>!IsDeleted</c> predicate re-excludes soft-deleted
    /// rows regardless of the ambient filter state. <c>IMultiTenant</c> still applies by ambient state (a source
    /// and its derived documents share a tenant).
    /// </para>
    /// <para>
    /// Contrast <see cref="AnyByOriginAsync"/>, whose soft-delete visibility is left to the caller's ambient filter
    /// state precisely because its two callers want opposite semantics. The divergence is deliberate, not drift.
    /// </para>
    /// </summary>
    Task<bool> AnyLiveDerivedDuplicateAsync(
        Guid originDocumentId,
        string originConstituentKey,
        Guid excludeDocumentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether any <see cref="Document"/> carries <paramref name="originDocumentId"/> as its
    /// <see cref="Document.OriginDocumentId"/> — i.e. whether that source still has derived sub-documents.
    /// Existence-only (compiles to SQL <c>EXISTS</c>, no row materialization), served by the leading (and only)
    /// column of the plain <c>OriginDocumentId</c> index.
    /// <para>
    /// Backs both <c>DocumentAppService</c> delete guards (#508). Soft-delete visibility is the <b>caller's</b>
    /// choice, expressed through the ambient <c>IDataFilter</c> state rather than a parameter:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>DeleteAsync</c> calls it with the <c>ISoftDelete</c> filter ON, so only <b>live</b>
    /// children count — a source whose sub-documents are all already in the recycle bin stays soft-deletable.</description></item>
    /// <item><description><c>PermanentDeleteAsync</c> calls it from inside its
    /// <c>DataFilter.Disable&lt;ISoftDelete&gt;()</c> scope, so recycle-bin children count too — hard-deleting the
    /// source reclaims the blob they reach through <c>OriginDocumentId</c>, and a recycle-bin child is
    /// restorable.</description></item>
    /// </list>
    /// <para>
    /// <c>IMultiTenant</c> always applies by ambient state (a source and its derived documents share a tenant), so
    /// the check never leaves the source's own layer.
    /// </para>
    /// <para>
    /// Contrast <see cref="AnyLiveDerivedDuplicateAsync"/>, which deliberately pins <c>!IsDeleted</c> in its own
    /// predicate: it has a single caller that always wants live-only yet runs inside a
    /// <c>Disable&lt;ISoftDelete&gt;()</c> scope. This method has two callers wanting opposite semantics, so it must
    /// <b>not</b> pin the predicate. Both directions of that choice are locked by <c>DocumentParentDelete_Tests</c>.
    /// </para>
    /// </summary>
    Task<bool> AnyByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Duplicate-detection candidate query (#411): <b>other</b> documents in the current layer with the same
    /// <see cref="Document.DocumentTypeId"/> and the same <see cref="Document.FieldFingerprint"/> as the document
    /// being extracted. Two documents sharing a (type, fingerprint) are likely the same business entity re-uploaded.
    /// Returns a lightweight <see cref="DuplicateCandidateModel"/> projection (Id + Title + file name + upload time)
    /// so the operator can recognize each candidate without loading full rows; the field extraction stage uses only
    /// the count to decide whether to flag <see cref="DocumentReviewReasons.DuplicateSuspected"/>.
    /// <para>
    /// <paramref name="documentId"/> (the document being checked) is excluded so a document never matches itself.
    /// <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters apply automatically by ambient state, so the result
    /// stays within the document's own layer and excludes recycle-bin documents. The result is hard-capped at
    /// <paramref name="maxResults"/> (<c>DocumentConsts.MaxDuplicateCandidates</c>) — a fingerprint shared by many
    /// documents never returns an unbounded set. Pure EF Core LINQ (equality on the indexed fingerprint column,
    /// projected with <c>AsNoTracking</c>), no raw SQL.
    /// </para>
    /// </summary>
    /// <param name="detectionScope">
    /// #651: the type's own <c>DuplicateScope</c> — <b>what counts as a duplicate</b>. Under
    /// <see cref="DuplicateDetectionScope.Uploader"/> a candidate must additionally share
    /// <paramref name="subjectCreatorId"/>; under <see cref="DuplicateDetectionScope.Layer"/> (the default and the
    /// pre-#651 behaviour) it adds no predicate at all. Applied <b>before</b> <paramref name="readScope"/>,
    /// because otherwise the operator panel would list candidates that never triggered the flag.
    /// </param>
    /// <param name="subjectCreatorId">
    /// The subject document's own <see cref="Document.CreatorId"/>, the ownership anchor #635's owner arm uses.
    /// Ignored under <see cref="DuplicateDetectionScope.Layer"/>. Under
    /// <see cref="DuplicateDetectionScope.Uploader"/> it is matched by <b>equality</b>, so a <c>null</c> anchor
    /// matches only another <c>null</c> — no fallback to layer-wide, which would apply the stricter rule to
    /// exactly the rows nobody asked to be stricter about (historical rows and every machine-identity upload,
    /// which have no <c>CurrentUser.Id</c> to record).
    /// </param>
    /// <param name="readScope">
    /// #635: the caller's read scope, applied as an additional predicate — <b>whose names may be shown</b>, which
    /// is orthogonal to <paramref name="detectionScope"/>. The panel names <b>other people's</b>
    /// documents by title and file name, so a caller who may not read them must not be handed them through the
    /// review detail of a document they can read — a (type, fingerprint) pair is not a permission to see whoever
    /// else uploaded one. The pipeline passes <see cref="DocumentAccessScope.Unrestricted"/>: it runs with no
    /// principal and only counts, and a duplicate the uploader may not see is still a duplicate.
    /// </param>
    Task<List<DuplicateCandidateModel>> FindDuplicateCandidatesAsync(
        Guid documentId,
        Guid documentTypeId,
        string fieldFingerprint,
        int maxResults,
        DuplicateDetectionScope detectionScope,
        Guid? subjectCreatorId,
        DocumentAccessScope readScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 1 of duplicate-scope reconciliation (#651 §5): how many <b>live</b> documents of one type sit in each
    /// collision bucket under <paramref name="detectionScope"/>, restricted to the fingerprints the caller names
    /// — <c>GROUP BY FieldFingerprint</c> for <see cref="DuplicateDetectionScope.Layer"/>,
    /// <c>GROUP BY FieldFingerprint, CreatorId</c> for <see cref="DuplicateDetectionScope.Uploader"/>. The
    /// existing <c>(TenantId, DocumentTypeId, FieldFingerprint)</c> index serves both the restriction and the
    /// grouping.
    /// <para>
    /// <b>Soft-deleted rows are excluded</b> (the ambient <c>ISoftDelete</c> filter is left on), matching
    /// <see cref="FindDuplicateCandidatesAsync"/> — a recycle-bin document is not a live collision for anyone.
    /// That is also why there is <b>no <c>HAVING COUNT(*) &gt; 1</c></b>: a recycle-bin row is absent from its own
    /// bucket, so a bucket of size 1 still means "this deleted document would collide on restore", and dropping
    /// singletons would silently leave every such row stale.
    /// </para>
    /// <para><c>IMultiTenant</c> stays on, so the aggregate never leaves this type's own layer. Pure EF LINQ, no raw SQL.</para>
    /// </summary>
    /// <param name="fingerprints">
    /// The fingerprints to aggregate — <b>this is the cap</b>, and the reason no <c>Take(N)</c> appears here. The
    /// caller is the reconciliation job, which asks only about the fingerprints on the page it is about to
    /// reconcile, so the result is bounded by <c>DocumentConsts.ReprocessingDispatchBatchSize</c> rather than by
    /// the type's distinct-fingerprint count. A <c>Take</c> would be the wrong instrument regardless: it bounds
    /// nothing a caller reads back, and it would hand the job a silently wrong verdict for every bucket past the
    /// cut. An empty collection short-circuits to an empty dictionary without querying.
    /// <para>
    /// Typed as <see cref="IReadOnlyCollection{T}"/> on purpose, the same shape
    /// <c>DocumentAccessScope.ToPredicate</c> uses: <c>Contains</c> then binds to <c>Enumerable.Contains</c>,
    /// which the relational provider translates to an <c>IN</c> list. <c>IReadOnlySet&lt;T&gt;.Contains</c> is an
    /// interface method the provider has no reason to know.
    /// </para>
    /// </param>
    Task<Dictionary<DuplicateCollisionKey, int>> CountDuplicateCollisionsAsync(
        Guid documentTypeId,
        DuplicateDetectionScope detectionScope,
        IReadOnlyCollection<string> fingerprints,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 2 of duplicate-scope reconciliation (#651 §5): a keyset-paged, narrow projection of one type's
    /// documents — everything the diff needs and nothing else, notably no <c>Markdown</c>. Same cursor contract as
    /// <see cref="GetIdsWithDuplicateBasisAsync"/> (<c>Id &gt; afterId</c>, ordered by <c>Id</c>, capped at
    /// <paramref name="maxCount"/>).
    /// <para>
    /// <b>Traverses soft delete</b>, unlike <see cref="CountDuplicateCollisionsAsync"/>: restoring a document must
    /// not bring back a flag derived from a rule the admin has since retracted. The two soft-delete scopes
    /// differing is the part of this that is easy to get wrong, so they are stated on both methods.
    /// <c>IMultiTenant</c> stays on.
    /// </para>
    /// </summary>
    Task<List<DuplicateReconciliationRow>> GetDuplicateReconciliationPageAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default);

    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a Document by Id and eager-loads its <b>field-stage</b> child collection —
    /// (#527) <see cref="Document.FieldValidationWarnings"/>. Written and cleared by the field-extraction write
    /// phase and the §7 type-change clearing, so the reconcile path (<see cref="Document.ReplaceFieldValidationWarnings"/>
    /// and the clearing transitions) needs the existing rows present to delete old / update in place / insert new.
    /// <para>
    /// Semantics match <c>FindAsync(id, includeDetails: true)</c>: returns <c>null</c> when not found;
    /// <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters are applied automatically according to ambient state.
    /// </para>
    /// <para>
    /// #216: after <c>DocumentPipelineRun</c> was split into an independent aggregate root, PipelineRun is no longer loaded with Document.
    /// Orchestration paths now use <see cref="Pipelines.IDocumentPipelineRunRepository"/>.
    /// </para>
    /// </summary>
    Task<Document?> FindWithFieldValuesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The v3 answer to "does any document hold a value for this field" (#559). Fail-closed direction — a
    /// field whose values already exist may not change its field type, because those values were validated
    /// against the old type and would render and index as nothing under the new one.
    /// <para>
    /// Two paths, because the value bag is an opaque JSON column and no provider can push a bag-key
    /// predicate into it:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Indexable and searchable field</b> — answered from the derived index by <c>FieldId</c>, one
    ///     indexed lookup. Exact, because every value of such a field has an index row by construction.
    ///   </item>
    ///   <item>
    ///     <b>Otherwise</b> (the field type is not indexable at all, e.g. long text, or the admin turned
    ///     <c>IsSearchable</c> off) — the index is legitimately empty regardless of the values, so it
    ///     cannot answer, and this falls back to paging the type's documents and testing the bag key in
    ///     memory, stopping at the first hit. Confined to that minority of fields on purpose: the scan is
    ///     bounded only by the type's document count.
    ///   </item>
    /// </list>
    /// <para>
    /// Soft-deleted documents count, as they did under v2: their values survive deletion and come back on
    /// restore. <c>IMultiTenant</c> stays on, so the answer is scoped to the field's own layer.
    /// </para>
    /// </summary>
    Task<bool> AnyFlexFieldValueAsync(
        Fields.Field field,
        bool isIndexable,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scope count for bulk reprocessing (#289), used by preview modals. Returns the number of documents in the current ambient layer
    /// (<c>IMultiTenant</c> + <c>ISoftDelete</c> global filters isolate automatically by ambient state; soft-deleted / trash documents do not count),
    /// with completed text extraction (<c>Markdown</c> non-empty because reclassification / field extraction both require text payload),
    /// that match the scope conditions.
    /// <para>Scope conditions (ANDed together):</para>
    /// <list type="bullet">
    ///   <item><paramref name="documentTypeId"/> non-null -> only that type (field re-extraction is always type-scoped; reclassification "only documents already assigned to this type" scope). Null -> no type limit (reclassification "all / cross-type" scope).</item>
    ///   <item><paramref name="withReason"/> non-null -> only documents containing that review reason. The reclassification "review queue" scope passes <see cref="DocumentReviewReasons.UnresolvedClassification"/>, the old PendingReview, under the #284 two-axis model.</item>
    ///   <item><paramref name="excludeManuallyConfirmed"/> = true -> exclude <see cref="DocumentReviewDisposition.Confirmed"/> to protect operator-confirmed documents, enabled by default for #289.</item>
    /// </list>
    /// </summary>
    Task<long> CountForReprocessingAsync(
        Guid? documentTypeId,
        DocumentReviewReasons? withReason,
        bool excludeManuallyConfirmed,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only Id query for keyset pagination in bulk reprocessing dispatch (#289). Returns the first <paramref name="maxCount"/>
    /// document Ids in the same scope as <see cref="CountForReprocessingAsync"/>, with <c>Id &gt; <paramref name="afterId"/></c>
    /// (<paramref name="afterId"/> null means from the beginning), ordered by <c>Id</c> ascending. Uses <c>AsNoTracking</c> +
    /// <c>Select(d =&gt; d.Id)</c> and never reads full rows, especially Markdown, to avoid OOM. The dispatcher uses the last Id
    /// as the cursor for the next chained batch.
    /// </summary>
    Task<List<Guid>> GetIdsForReprocessingAsync(
        Guid? documentTypeId,
        DocumentReviewReasons? withReason,
        bool excludeManuallyConfirmed,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyset-paginated Ids of documents carrying a <see cref="DocumentFieldValidationWarning"/> for
    /// <paramref name="fieldDefinitionId"/> (#528) — the cleanup scope after that field definition is deleted, since
    /// a warning naming a field that no longer exists keeps the blocking
    /// <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit set and parks the document out of
    /// <c>DocumentReadyEto</c> forever.
    /// <para>
    /// <b>Traverses soft delete</b> (the implementation disables <c>ISoftDelete</c>, like
    /// <see cref="FindByContentHashAsync"/>): a recycle-bin document must be cleaned too, otherwise restoring it
    /// resurrects review state for a field that no longer exists. The caller distinguishes live from recycle-bin rows
    /// by reading <c>Document.IsDeleted</c> per row, because only live documents need lifecycle re-derivation.
    /// <c>IMultiTenant</c> still applies by ambient state, so the scan never leaves the field definition's own layer.
    /// </para>
    /// <para>
    /// Ordered by <c>Id</c> with <c>Id &gt; afterId</c> (null = from the beginning), <c>AsNoTracking</c> +
    /// <c>Select(d =&gt; d.Id)</c> so no full row (especially Markdown) is ever materialized. The caller chains the
    /// next batch with the last Id as the cursor.
    /// </para>
    /// </summary>
    Task<List<Guid>> GetIdsWithFieldValidationWarningAsync(
        Guid fieldDefinitionId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyset-paginated Ids of documents under <paramref name="documentTypeId"/> that still carry a duplicate basis —
    /// a non-null <see cref="Document.FieldFingerprint"/> or the <see cref="DocumentReviewReasons.DuplicateSuspected"/>
    /// reason (#528).
    /// <para>
    /// Used only when a deleted field definition was the type's <b>last active unique-key field</b>. The type then has
    /// no duplicate basis left, every document's fingerprint should be <c>null</c>, and any lingering
    /// <c>DuplicateSuspected</c> is definitively obsolete — a blocking false park. Deleting one of <i>several</i>
    /// unique-key fields is <b>not</b> this case and must not use this scan: narrowing the key preserves equality, so
    /// existing flags stay valid and clearing them would hide real duplicates (tracked as under-detection in #537).
    /// </para>
    /// <para>
    /// Same contract as <see cref="GetIdsWithFieldValidationWarningAsync"/>: traverses soft delete, keeps
    /// <c>IMultiTenant</c>, Ids only, keyset cursor.
    /// </para>
    /// </summary>
    Task<List<Guid>> GetIdsWithDuplicateBasisAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyset-paginated Ids of every document bound to <paramref name="documentTypeId"/> — the scope a field
    /// rename must rewrite value-bag keys over.
    /// <para>
    /// <b>Why this exists rather than the kernel's own walk.</b> <c>IFlexFieldValueMigrator&lt;Document&gt;</c>
    /// renames by name alone, across every document the tenant has, because the kernel's model is one field
    /// name per host <i>type</i>. Vault Extract's is not: a field is unique per
    /// <c>(TenantId, DocumentTypeId, Name)</c>, so two document types may legitimately both define
    /// <c>invoice_no</c>, and renaming one type's would silently rewrite the other type's values into a key no
    /// definition backs — invisible on read, still indexed, unrecoverable without knowing the old name. Scoping
    /// the walk is the whole fix, and only Vault Extract can do it: <c>DocumentTypeId</c> is an indexed column
    /// here, which is exactly the predicate the kernel says it cannot push down through the provider.
    /// </para>
    /// <para>
    /// <b>Traverses soft delete</b>, like <see cref="GetIdsWithFieldValidationWarningAsync"/>: a recycle-bin
    /// document restored later must come back with its values under the current name, not the one they were
    /// stored under before the rename. <c>IMultiTenant</c> still applies by ambient state, so the scan never
    /// leaves the field's own layer.
    /// </para>
    /// <para>
    /// Ids only (<c>AsNoTracking</c> + <c>Select</c>), so the cursor scan never materializes Markdown; the
    /// caller loads each page's documents by id to mutate them. Ordered by <c>Id</c> with
    /// <c>Id &gt; afterId</c> (null = from the beginning).
    /// </para>
    /// </summary>
    Task<List<Guid>> GetIdsByDocumentTypeAsync(
        Guid documentTypeId,
        Guid? afterId,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate overview statistics for the current ambient layer (#333): per-lifecycle document counts,
    /// the needs-review count, and the total original uploaded size (sum of <c>FileOrigin.FileSize</c>).
    /// <para>
    /// <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters apply automatically by ambient state, so the result
    /// covers only the current layer's non-deleted documents (active tenant -> that tenant; no tenant -> Host).
    /// Neither filter is disabled, keeping statistics within a single layer and excluding the recycle bin.
    /// </para>
    /// <para>
    /// Needs-review uses the canonical review-queue predicate
    /// (<c>ReviewReasons != None &amp;&amp; ReviewDisposition != Rejected</c>, shared with <c>DocumentAppService.ApplyFilter</c>);
    /// it overlaps the lifecycle buckets and is not part of the <c>TotalCount</c> partition.
    /// </para>
    /// <para>
    /// The storage sum also excludes every derived sub-document (<c>OriginDocumentId != null</c>), on top of the
    /// existing #346 container exclusion — a derived document has no <c>FileOrigin</c> of its own (<c>null</c>), so
    /// it contributes nothing to physical storage. Document counts are unaffected: a derived sub-document is still
    /// a real document and counts normally in its lifecycle bucket.
    /// </para>
    /// </summary>
    Task<DocumentStatisticsModel> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
