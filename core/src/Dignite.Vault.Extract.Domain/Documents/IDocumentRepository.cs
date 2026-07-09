using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents;

public interface IDocumentRepository : IRepository<Document, Guid>
{
    Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a <b>non-derived</b> document by exact upload <c>FileOrigin.ContentHash</c> — the #221 upload-time
    /// dedup check (<c>DocumentAppService.UploadAsync</c>). #481: scoped to <c>OriginDocumentId == null</c> — a
    /// derived sub-document shares its parent's blob/hash (a text-slice child shares the whole bundle's hash; a
    /// figure child shares the retained image's hash), so an unscoped check would nondeterministically report a
    /// child row as "the duplicate". A re-uploaded bundle is still caught via its parent (non-derived) row;
    /// deliberately re-uploading a standalone image that also happens to exist as a figure child is treated as a
    /// legitimate new document — cross-artifact business-level duplicate detection is #411's fingerprint concern,
    /// not this upload-time check.
    /// <para>
    /// Traverses soft-delete (the implementation disables <c>ISoftDelete</c>, like the sibling shared-blob checks on
    /// this repository): a re-upload matching a recycle-bin document's hash must still surface as
    /// <c>Document.InRecycleBin</c>, not silently be accepted as new.
    /// </para>
    /// </summary>
    Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether any document other than <paramref name="excludeDocumentId"/> has a <c>FileOrigin.BlobName</c> equal to
    /// <paramref name="blobName"/> — the #478 shared-blob reference check used by
    /// <c>DocumentAppService.PermanentDeleteAsync</c> before reclaiming a blob a figure sub-document may share with
    /// its source. Existence-only (SQL <c>EXISTS</c>, no row materialization).
    /// <para>
    /// The implementation disables the <c>ISoftDelete</c> filter itself (like <see cref="FindByContentHashAsync"/>):
    /// a recycle-bin document is restorable, so it still counts as a live reference; a hard-deleted row no longer
    /// exists and correctly does not. <c>IMultiTenant</c> still applies by ambient state (source and sub-documents
    /// share a tenant).
    /// </para>
    /// </summary>
    Task<bool> AnyWithFileOriginBlobNameAsync(
        string blobName,
        Guid? excludeDocumentId = null,
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
    /// Finds a Document by Id and eager-loads <b>only</b> the <see cref="Document.ExtractedFieldValues"/> child collection.
    /// Field extraction write-back paths (<c>FieldExtractionEventHandler</c>) need existing field rows present so
    /// <see cref="Document.SetFields"/> can reconcile correctly (delete old / update in place / insert new).
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
    /// #481: the storage sum additionally excludes every derived sub-document (<c>OriginDocumentId != null</c>), on
    /// top of the existing #346 container exclusion — a derived document shares its parent's
    /// <c>FileOrigin.FileSize</c> (a text-slice child shares the whole bundle's size; a figure child shares the
    /// retained image's size) rather than owning distinct bytes, so summing it too would multiply the same storage
    /// by however many children exist. Document counts are unaffected: a derived sub-document is still a real
    /// document and counts normally in its lifecycle bucket.
    /// </para>
    /// </summary>
    Task<DocumentStatisticsModel> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
