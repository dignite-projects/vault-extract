using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    Task<List<DuplicateCandidateModel>> FindDuplicateCandidatesAsync(
        Guid documentId,
        Guid documentTypeId,
        string fieldFingerprint,
        int maxResults,
        CancellationToken cancellationToken = default);

    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a Document by Id and eager-loads its <b>field-stage</b> child collections —
    /// <see cref="Document.ExtractedFieldValues"/> and (#527) <see cref="Document.FieldValidationWarnings"/>. Both are
    /// written and cleared together by the field-extraction write phase and the §7 type-change clearing, so the reconcile
    /// paths (<see cref="Document.SetFields"/> / <see cref="Document.ReplaceFieldValidationWarnings"/> and the clearing
    /// transitions) need the existing rows present to delete old / update in place / insert new. The two collections are
    /// loaded with split queries (single-document scope) to avoid the #206 Cartesian product.
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
    /// Field value matching subquery for structured retrieval (field architecture v2 / Issue #206 + #207): returns document Ids in the current layer
    /// (ABP <c>IMultiTenant</c> + soft-delete global filters isolate automatically by ambient state), where
    /// <see cref="Document.DocumentTypeId"/> == <paramref name="documentTypeId"/> and
    /// <see cref="Document.ExtractedFieldValues"/> satisfies <paramref name="fieldQueries"/>. Multiple queries are combined with <c>AND</c>,
    /// following structured retrieval convention: different fields narrow each other. The caller layer (<c>DocumentAppService.GetListAsync</c>)
    /// intersects this with metadata filters using <c>query.Where(ids.Contains(d.Id))</c>.
    /// <para>
    /// Implementations start from the <c>Documents</c> aggregate root. Each field filter compiles into an <c>Any</c> over the child collection
    /// <see cref="Document.ExtractedFieldValues"/> (EXISTS, matching the child by <see cref="DocumentFieldQuery.FieldDefinitionId"/>)
    /// plus ordinary typed-column comparisons. This is pure EF Core LINQ and translates to SQL Server / PostgreSQL / MySQL / SQLite.
    /// It no longer depends on SQL Server <c>JSON_VALUE</c> / <c>TRY_CONVERT</c> / raw SQL, eliminating the injection surface.
    /// </para>
    /// Safety: dispatch equality / range by <see cref="DocumentFieldQuery.FieldDataType"/>; only = + range, never LIKE.
    /// Passing ranges for Text/Boolean throws <see cref="VaultExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange"/>;
    /// values that cannot parse as the declared type throw <see cref="VaultExtractErrorCodes.ExtractedField.InvalidValue"/>.
    /// Both are loud failures, never silent empty results.
    /// Authorization assertions, input validation (required / length / count / at least one value), and field resolution (external documentTypeCode / fieldName -> internal
    /// <see cref="Document.DocumentTypeId"/> / <see cref="DocumentFieldQuery.FieldDefinitionId"/> + <see cref="FieldDataType"/>）
    /// all belong to the caller layer (DTO + AppService). This repository only handles data access for the <see cref="Document"/> aggregate root,
    /// does not repeat those checks here, and does not depend on repositories for other aggregates.
    /// </summary>
    /// <param name="documentTypeId">Single document type Id that anchors retrieval, resolved from documentTypeCode by the caller layer and applied as a SQL parameter.</param>
    /// <param name="fieldQueries">Resolved field value filters; each carries <c>FieldDefinitionId</c> + <c>FieldDataType</c> + at least one value. Empty -> returns an empty collection.</param>
    Task<List<Guid>> GetFieldMatchedIdsAsync(
        Guid documentTypeId,
        IReadOnlyList<DocumentFieldQuery> fieldQueries,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether any <see cref="DocumentExtractedField"/> field value row references <paramref name="fieldDefinitionId"/> (#207).
    /// Used by <c>FieldDefinitionAppService.UpdateAsync</c> as a DataType change guard: fields with existing extracted values cannot change DataType,
    /// otherwise historical values remain in old typed columns and silently disappear from queries by the new type.
    /// <para>
    /// Scans the child <c>DbSet</c> directly. It is not constrained by the parent Document's <c>ISoftDelete</c> filter:
    /// even when the referenced document is soft-deleted, its field rows remain and revive on restore, so they should count too (conservative fail-closed).
    /// <c>IMultiTenant</c> still isolates by ambient tenant, matching field definitions in the current layer.
    /// </para>
    /// </summary>
    Task<bool> AnyExtractedFieldValueAsync(
        Guid fieldDefinitionId,
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
