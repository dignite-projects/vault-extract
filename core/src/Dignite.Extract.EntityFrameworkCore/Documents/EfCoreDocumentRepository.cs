using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Extract;
using Dignite.Extract.Documents;
using Dignite.Extract.Documents.Fields;
using Dignite.Extract.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Extract.Documents;

public class EfCoreDocumentRepository
    : EfCoreRepository<ExtractDbContext, Document, Guid>, IDocumentRepository
{
    public EfCoreDocumentRepository(
        IDbContextProvider<ExtractDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .FirstOrDefaultAsync(
                d => d.FileOrigin != null && d.FileOrigin.BlobName == blobName,
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await dbSet
                .FirstOrDefaultAsync(
                    d => d.FileOrigin != null && d.FileOrigin.ContentHash == contentHash,
                    GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<List<DuplicateCandidateModel>> FindDuplicateCandidatesAsync(
        Guid documentId,
        Guid documentTypeId,
        string fieldFingerprint,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        // #411: other documents in the current layer sharing this (type, fingerprint). The default IMultiTenant +
        // ISoftDelete global filters are intentionally NOT disabled, so the result stays within the document's own
        // layer and excludes recycle-bin documents. Equality on the indexed FieldFingerprint column; AsNoTracking +
        // a scalar projection (Id/Title/file name/upload time, no Markdown) keeps it light; Take is the fail-closed
        // cap on a widely-shared fingerprint.
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .AsNoTracking()
            .Where(d => d.DocumentTypeId == documentTypeId
                     && d.FieldFingerprint == fieldFingerprint
                     && d.Id != documentId)
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

    public virtual async Task<List<Document>> GetListByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default)
    {
        // #349: list a source's derived sub-documents. The composite unique index (OriginDocumentId, OriginConstituentKey)
        // has OriginDocumentId as its leading column, so this filter is index-served (no new index needed). IMultiTenant
        // + ISoftDelete global filters apply automatically by ambient state — only the container's own layer, no
        // already-deleted sub-documents. Scalar fields + owned FileOrigin suffice for the soft-delete + ETO publication.
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(d => d.OriginDocumentId == originDocumentId)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<bool> AnyByOriginAsync(
        Guid originDocumentId,
        CancellationToken cancellationToken = default)
    {
        // Existence-only guard for DeleteAsync: does this source still have any LIVE derived sub-document? The default
        // IMultiTenant + ISoftDelete global filters apply, so already-soft-deleted children do not count (a source whose
        // sub-documents are all already in the recycle bin can still be deleted) and the check stays within the source's
        // layer. Index-served by the leading OriginDocumentId column of the (OriginDocumentId, OriginConstituentKey) unique index.
        var dbSet = await GetDbSetAsync();
        return await dbSet.AnyAsync(
            d => d.OriginDocumentId == originDocumentId,
            GetCancellationToken(cancellationToken));
    }

    public override async Task<IQueryable<Document>> WithDetailsAsync()
    {
        return (await GetQueryableAsync()).IncludeDetails();
    }

    public virtual async Task<Document?> FindWithFieldValuesAsync(
        Guid id, CancellationToken cancellationToken = default)
    {
        var query = await WithDetailsAsync(d => d.ExtractedFieldValues);
        return await query.FirstOrDefaultAsync(
            d => d.Id == id, GetCancellationToken(cancellationToken));
    }

    public virtual async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Traverse only soft delete to physically delete already-soft-deleted rows, while preserving the IMultiTenant tenant boundary.
        // Never use IgnoreQueryFilters(), because it would also disable IMultiTenant and allow future callers without app-layer tenant validation
        // to hard-delete across tenants (#220).
        // ExecuteDeleteAsync relies on DB-level ON DELETE CASCADE. All three child FKs — DocumentExtractedField,
        // DocumentPipelineRun, and DocumentSegment (#346/#371) — use OnDelete(Cascade), and the narrowed filter does not affect cascading.
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbContext = await GetDbContextAsync();
            await dbContext.Set<Document>()
                .Where(d => d.Id == id)
                .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
        }
    }

    public virtual async Task<List<Guid>> GetFieldMatchedIdsAsync(
        Guid documentTypeId,
        IReadOnlyList<DocumentFieldQuery> fieldQueries,
        CancellationToken cancellationToken = default)
    {
        // Caller layer (DocumentAppService.GetListAsync) calls this only when field filters exist, and has already validated required documentTypeCode,
        // field count / length / at least one value (DTO + AppService layer, loud AbpValidationException), then resolved documentTypeCode /
        // fieldName to internal Ids. This guard defends direct empty input.
        if (fieldQueries is not { Count: > 0 })
        {
            return new List<Guid>();
        }

        var dbSet = await GetDbSetAsync();

        // Field value filtering starts from the Documents aggregate root. Tenant (IMultiTenant) + soft-delete (ISoftDelete)
        // global filters are applied automatically by ambient state (Issue #206: no filter disabling and no hand-written TenantId predicate).
        // documentTypeId anchors one type because field values have no stable meaning outside a type.
        // Each field filter compiles to ExtractedFieldValues.Any (EXISTS, matching the child by FieldDefinitionId), and multiple fields are ANDed
        // following structured retrieval convention: different fields narrow each other. Ordinary column comparisons (= / range) are portable
        // across relational databases, no longer relying on SQL Server JSON_VALUE / TRY_CONVERT / raw SQL, eliminating the injection surface.
        var query = dbSet.Where(d => d.DocumentTypeId == documentTypeId);

        foreach (var fieldQuery in fieldQueries)
        {
            query = ApplyFieldValueFilter(query, fieldQuery);
        }

        return await query
            .AsNoTracking()
            .Select(d => d.Id)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<bool> AnyExtractedFieldValueAsync(
        Guid fieldDefinitionId,
        CancellationToken cancellationToken = default)
    {
        // Scan the child DbSet directly, bypassing the aggregate root, to answer "does any field value still reference this FieldDefinition".
        // This is not constrained by the parent Document's ISoftDelete filter because the child has no ISoftDelete and its DbSet does not apply parent filters.
        // Field rows for soft-deleted documents still exist and revive on restore, so count them too. IMultiTenant still isolates by ambient tenant,
        // matching field definitions in the current layer.
        var dbContext = await GetDbContextAsync();
        return await dbContext.Set<DocumentExtractedField>()
            .AnyAsync(f => f.FieldDefinitionId == fieldDefinitionId, GetCancellationToken(cancellationToken));
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
            .Select(g => new
            {
                Status = g.Key,
                // #346: a container is an infrastructure wrapper, not a business document — its sub-documents are the
                // real records. Exclude containers from the document counts / storage so a container + its N
                // sub-documents do not double-count. But a segmentation-incomplete container DOES need operator
                // attention, so it is INCLUDED in NeedsReview below — the review-queue list (DocumentAppService.ApplyFilter)
                // counts it too, so the dashboard count and the queue never drift (#333).
                Count = g.Sum(d => d.IsContainer ? 0L : 1L),
                Bytes = g.Sum(d => d.IsContainer ? 0L : (d.FileOrigin != null ? d.FileOrigin.FileSize : 0L)),
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

    /// <summary>
    /// Compiles one field value query into an <c>Any</c> (EXISTS) predicate over <see cref="Document.ExtractedFieldValues"/>,
    /// dispatching by <see cref="FieldDataType"/> to ordinary comparisons on the corresponding typed column:
    /// <list type="bullet">
    ///   <item><c>Text</c> / <c>Boolean</c>: equality only (red line: never LIKE); passing a range throws
    ///   <see cref="ExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange"/> as a correctable signal for AI clients.</item>
    ///   <item><c>Number</c> / <c>Date</c> / <c>DateTime</c>: equality or inclusive range.
    ///   Inputs that cannot parse as the declared type throw <see cref="ExtractErrorCodes.ExtractedField.InvalidValue"/> loudly, not silent empty.</item>
    /// </list>
    /// Equality is uniformly represented as a degenerate interval <c>[v, v]</c>, sharing the same predicate shape as ranges and removing equality/range branch duplication.
    /// </summary>
    private static IQueryable<Document> ApplyFieldValueFilter(
        IQueryable<Document> query,
        DocumentFieldQuery fieldQuery)
    {
        // Internally match child rows by FieldDefinitionId (#207, no longer by field name string). FieldName is only for readable diagnostics in error messages.
        var fieldDefinitionId = fieldQuery.FieldDefinitionId;
        var name = fieldQuery.FieldName;

        // Fail-closed: provide at least equality or range. Completely empty means malformed query and loud-fails, matching the DocumentFieldQuery contract.
        // It must never degrade into "fetch everything of this type". Caller-layer DTO already validates this; this is defense in depth for direct repository calls.
        if (fieldQuery.FieldValue == null && fieldQuery.FieldValueMin == null && fieldQuery.FieldValueMax == null)
        {
            throw InvalidValue(name, fieldQuery.FieldDataType);
        }

        var isRange = fieldQuery.FieldValue == null
            && (fieldQuery.FieldValueMin != null || fieldQuery.FieldValueMax != null);

        switch (fieldQuery.FieldDataType)
        {
            case FieldDataType.Text:
                if (isRange)
                {
                    throw RangeNotSupported(name, fieldQuery.FieldDataType);
                }
                var textValue = fieldQuery.FieldValue!;
                return query.Where(d => d.ExtractedFieldValues
                    .Any(f => f.FieldDefinitionId == fieldDefinitionId && f.TextValue == textValue));

            case FieldDataType.LongText:
                // LongText fields live in nvarchar(max) columns and are not indexed. Red line: never use them as query conditions
                // (equality / range / LIKE are all forbidden). Long-content search belongs to downstream RAG (CLAUDE.md OUT of scope).
                // Loud-fail as a correctable signal for AI clients, never degrade into fetching everything.
                throw NotQueryable(name, fieldQuery.FieldDataType);

            case FieldDataType.Boolean:
                if (isRange)
                {
                    throw RangeNotSupported(name, fieldQuery.FieldDataType);
                }
                if (!bool.TryParse(fieldQuery.FieldValue, out var boolValue))
                {
                    throw InvalidValue(name, fieldQuery.FieldDataType);
                }
                return query.Where(d => d.ExtractedFieldValues
                    .Any(f => f.FieldDefinitionId == fieldDefinitionId && f.BooleanValue == boolValue));

            case FieldDataType.Number:
            {
                var (min, max) = ParseRange(fieldQuery, ParseDecimal);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.FieldDefinitionId == fieldDefinitionId
                    && (min == null || f.NumberValue >= min)
                    && (max == null || f.NumberValue <= max)));
            }

            case FieldDataType.Date:
            {
                var (min, max) = ParseRange(fieldQuery, ParseDate);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.FieldDefinitionId == fieldDefinitionId
                    && (min == null || f.DateValue >= min)
                    && (max == null || f.DateValue <= max)));
            }

            case FieldDataType.DateTime:
            {
                var (min, max) = ParseRange(fieldQuery, ParseDateTime);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.FieldDefinitionId == fieldDefinitionId
                    && (min == null || f.DateTimeValue >= min)
                    && (max == null || f.DateTimeValue <= max)));
            }

            default:
                throw InvalidValue(name, fieldQuery.FieldDataType);
        }
    }

    /// <summary>
    /// Parses a field query into typed inclusive <c>(min, max)</c> bounds: equality degenerates to <c>[v, v]</c>;
    /// range uses min / max, each nullable. Any input parse failure throws <see cref="ExtractErrorCodes.ExtractedField.InvalidValue"/> loudly.
    /// </summary>
    private static (T? Min, T? Max) ParseRange<T>(
        DocumentFieldQuery fieldQuery, Func<string, T?> parse)
        where T : struct
    {
        if (fieldQuery.FieldValue != null)
        {
            var value = parse(fieldQuery.FieldValue)
                ?? throw InvalidValue(fieldQuery.FieldName, fieldQuery.FieldDataType);
            return (value, value);
        }

        T? min = null;
        T? max = null;
        if (fieldQuery.FieldValueMin != null)
        {
            min = parse(fieldQuery.FieldValueMin)
                ?? throw InvalidValue(fieldQuery.FieldName, fieldQuery.FieldDataType);
        }
        if (fieldQuery.FieldValueMax != null)
        {
            max = parse(fieldQuery.FieldValueMax)
                ?? throw InvalidValue(fieldQuery.FieldName, fieldQuery.FieldDataType);
        }
        return (min, max);
    }

    private static decimal? ParseDecimal(string s)
        => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateOnly? ParseDate(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? DateOnly.FromDateTime(v)
            : null;

    // Accept only offset-free wall-clock ISO strings, consistent with storage-side datetime2 / DocumentExtractedField.SetValue.
    // Strings with offset / Z would be converted by .NET to server local time and break wall-clock storage semantics,
    // so treat them as dirty input and return null; the caller loud-fails.
    private static DateTime? ParseDateTime(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            && v.Kind == DateTimeKind.Unspecified
            ? v
            : null;

    private static BusinessException RangeNotSupported(string fieldName, FieldDataType dataType) =>
        new BusinessException(ExtractErrorCodes.ExtractedField.FieldTypeDoesNotSupportRange)
            .WithData("FieldName", fieldName)
            .WithData("DataType", dataType.ToString());

    private static BusinessException NotQueryable(string fieldName, FieldDataType dataType) =>
        new BusinessException(ExtractErrorCodes.ExtractedField.FieldTypeNotQueryable)
            .WithData("FieldName", fieldName)
            .WithData("DataType", dataType.ToString());

    private static BusinessException InvalidValue(string fieldName, FieldDataType dataType) =>
        new BusinessException(ExtractErrorCodes.ExtractedField.InvalidValue)
            .WithData("FieldName", fieldName)
            .WithData("DataType", dataType.ToString());
}
