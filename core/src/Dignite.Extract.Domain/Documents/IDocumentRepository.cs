using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Extract.Documents;

public interface IDocumentRepository : IRepository<Document, Guid>
{
    Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Duplicate-detection candidate query (#411): Ids of <b>other</b> documents in the current layer with the same
    /// <see cref="Document.DocumentTypeId"/> and the same <see cref="Document.FieldFingerprint"/> as the document
    /// being extracted. Two documents sharing a (type, fingerprint) are likely the same business entity re-uploaded.
    /// <para>
    /// <paramref name="documentId"/> (the document being checked) is excluded so a document never matches itself.
    /// <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters apply automatically by ambient state, so the result
    /// stays within the document's own layer and excludes recycle-bin documents. The result is hard-capped at
    /// <paramref name="maxResults"/> (<c>DocumentConsts.MaxDuplicateCandidates</c>) — a fingerprint shared by many
    /// documents never returns an unbounded set. Pure EF Core LINQ (equality on the indexed fingerprint column), no
    /// raw SQL.
    /// </para>
    /// </summary>
    Task<List<Guid>> FindDuplicateCandidateIdsAsync(
        Guid documentId,
        Guid documentTypeId,
        string fieldFingerprint,
        int maxResults,
        CancellationToken cancellationToken = default);

    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the derived sub-documents spawned from <paramref name="originDocumentId"/> (#306 / #346 Scenario B):
    /// every <see cref="Document"/> whose <see cref="Document.OriginDocumentId"/> equals it. Served by the composite
    /// unique index <c>(OriginDocumentId, OriginConstituentKey)</c> whose leading column is <c>OriginDocumentId</c>,
    /// so no extra index is needed.
    /// <para>
    /// Used by the #349 container-retraction handler (<c>ContainerMarkerClearedEventHandler</c>): when a container is
    /// reclassified to a concrete type, its previously-spawned sub-documents must be soft-deleted so they stop
    /// double-counting downstream. Scalar fields + owned <c>FileOrigin</c> suffice (no child collection), so this does
    /// not eager-load details. <c>IMultiTenant</c> + <c>ISoftDelete</c> global filters apply automatically by ambient
    /// state, keeping the result within the container's own layer and excluding already-deleted sub-documents.
    /// </para>
    /// </summary>
    Task<List<Document>> GetListByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="originDocumentId"/> still has any <b>live</b> derived sub-document (#306 / #346):
    /// a non-soft-deleted <see cref="Document"/> whose <see cref="Document.OriginDocumentId"/> equals it. Existence-only
    /// variant of <see cref="GetListByOriginAsync"/> (compiles to SQL <c>EXISTS</c>, no row materialization), used as the
    /// <c>DocumentAppService.DeleteAsync</c> guard: a source must not be sent to the recycle bin while its constituents
    /// are still live, otherwise their provenance back-reference would dangle. Served by the leading column of the
    /// composite unique index <c>(OriginDocumentId, OriginConstituentKey)</c>. <c>IMultiTenant</c> + <c>ISoftDelete</c>
    /// global filters apply automatically by ambient state, so already-deleted sub-documents do not count and the check
    /// stays within the source's own layer.
    /// </summary>
    Task<bool> AnyByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default);

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
    /// Passing ranges for Text/Boolean throws <see cref="ExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange"/>;
    /// values that cannot parse as the declared type throw <see cref="ExtractErrorCodes.ExtractedField.InvalidValue"/>.
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
    /// </summary>
    Task<DocumentStatisticsModel> GetStatisticsAsync(CancellationToken cancellationToken = default);
}
