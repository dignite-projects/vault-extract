using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Fields;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// #499: the document-scoped export. Replaces <c>ExportTemplateAppService</c>, whose saved column projections
/// were deleted along with the template layer.
/// </summary>
[Authorize(VaultExtractPermissions.Documents.Export)]
public class DocumentExportAppService : VaultExtractAppService, IDocumentExportAppService
{
    // Fixed exported system field headers (#207 / #287): LifecycleStatus / ReviewStatus (disposition axis) /
    // ReviewReasons (reason axis) / Title. Always exported, never configurable.
    // ReviewStatus column name is stable, with DB column name also ReviewStatus, and value taken from ReviewDisposition.
    private static readonly IReadOnlyList<string> SystemFieldHeaders = new[]
    {
        "LifecycleStatus", "ReviewStatus", "ReviewReasons", "Title"
    };

    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;

    public DocumentExportAppService(
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository)
    {
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
    }

    public virtual async Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input)
    {
        // An unknown type code loud-fails rather than yielding an empty file. The list may legitimately show an
        // empty page for a type that does not exist in this layer, but an export is an artifact the operator
        // will hand to an accountant — a header-only CSV is a silent lie about what the layer contains.
        var documentType = await _documentTypeRepository.FindByTypeCodeAsync(input.DocumentTypeCode);
        if (documentType == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), input.DocumentTypeCode);
        }

        // #499 decision (a): columns come from the type's LIVE field definitions, ordered by DisplayOrder — the
        // same rows, in the same order, that drive the operator list's dynamic columns. Values a document still
        // holds for an ARCHIVED (soft-deleted) definition are therefore absent from the file. The template path
        // used to traverse soft-delete to resolve columns it explicitly referenced; nothing references them now.
        var fieldDefinitions = await _fieldDefinitionRepository.GetListAsync(documentType.Id);

        // Tenant isolation is enforced by the ambient IMultiTenant filter, including GetQueryableAsync below.
        var query = await _documentRepository.GetQueryableAsync();
        query = query.Where(d => d.DocumentTypeId == documentType.Id);

        if (input.LifecycleStatus.HasValue)
            query = query.Where(d => d.LifecycleStatus == input.LifecycleStatus.Value);
        if (input.CabinetId.HasValue)
            query = query.Where(d => d.CabinetId == input.CabinetId.Value);

        // Sub-document provenance (#354): only the children derived from this source document.
        if (input.OriginDocumentId.HasValue)
            query = query.Where(d => d.OriginDocumentId == input.OriginDocumentId.Value);
        // Operator review queue (#284 / #395): reuses the canonical DocumentReviewQueries.RequiresAttention
        // expression the list and the needs-review badge already run. A second, hand-rolled "needs review"
        // predicate here is precisely how the exported file and the screen would quietly disagree.
        if (input.HasReviewReasons == true)
            query = query.Where(DocumentReviewQueries.RequiresAttention);

        if (input.CreationTimeMin.HasValue)
            query = query.Where(d => d.CreationTime >= input.CreationTimeMin.Value.Date);
        // Upper time bound includes the full Max date (< Max + 1 day), matching date picker intuition.
        if (input.CreationTimeMax.HasValue)
            query = query.Where(d => d.CreationTime < input.CreationTimeMax.Value.Date.AddDays(1));

        // Extracted-field-value filters, AND-combined with the metadata filters above. Resolve the field names
        // against the type (unknown field loud-fails via the shared resolver — the same path the document list /
        // MCP search use), match document ids through the EXISTS query (GetFieldMatchedIdsAsync; the ambient
        // IMultiTenant filter keeps it in the caller's layer), then intersect. The Take(limit + 1) below still
        // bounds the export size.
        if (input.FieldFilters is { Count: > 0 })
        {
            var fieldQueries = await DocumentFieldQueryResolver.ResolveAsync(
                _fieldDefinitionRepository, input.FieldFilters, documentType.Id, documentType.TypeCode);
            var matchedIds = await _documentRepository.GetFieldMatchedIdsAsync(documentType.Id, fieldQueries);
            query = query.Where(d => matchedIds.Contains(d.Id));
        }

        // Single fetch of (Max + 1) projected to ExportProjection (non-entity type -> does not SELECT Markdown and does not enter tracker).
        // Fetch one extra row to atomically detect over limit, eliminating silent truncation caused by concurrent inserts between count + Take queries.
        // In accounting scenarios, missing exported vouchers is more dangerous than an error, so over limit fails fast.
        var limit = DocumentExportConsts.MaxExportDocumentCount;
        var rows = await AsyncExecuter.ToListAsync(
            query
                .OrderByDescending(d => d.CreationTime)
                .Select(d => new ExportProjection
                {
                    Title = d.Title,
                    LifecycleStatus = d.LifecycleStatus,
                    ReviewDisposition = d.ReviewDisposition,
                    ReviewReasons = d.ReviewReasons,
                    // Typed child rows are projected with the document through a correlated subquery in one query, not per-document N+1.
                    ExtractedFields = d.ExtractedFieldValues
                        .Select(f => new ExtractedFieldProjection
                        {
                            FieldDefinitionId = f.FieldDefinitionId,
                            Order = f.Order,
                            TextValue = f.TextValue,
                            LongTextValue = f.LongTextValue,
                            BooleanValue = f.BooleanValue,
                            NumberValue = f.NumberValue,
                            DateValue = f.DateValue,
                            DateTimeValue = f.DateTimeValue,
                        })
                        .ToList(),
                })
                .Take(limit + 1));

        if (rows.Count > limit)
        {
            throw new BusinessException(VaultExtractErrorCodes.Export.DocumentLimitExceeded)
                .WithData("count", limit + "+")
                .WithData("max", limit);
        }

        // Fixed system field columns first, then one column per live field definition, in DisplayOrder.
        // Column headers use field DisplayName, so a field rename follows through automatically (#207).
        var headers = new List<string>(SystemFieldHeaders);
        headers.AddRange(fieldDefinitions.Select(f => f.DisplayName));

        var systemCount = SystemFieldHeaders.Count;
        var dataRows = rows
            .Select(r =>
            {
                var cells = new string?[headers.Count];
                cells[0] = r.LifecycleStatus.ToString();
                cells[1] = r.ReviewDisposition.ToString();
                // #287: reason axis. None -> empty cell (no unresolved reason); otherwise [Flags].ToString(), such as "MissingRequiredFields".
                // Empty vs non-empty is clearer than relying on "missing field cell left empty", which cannot distinguish optional empty from required missing.
                cells[2] = r.ReviewReasons == DocumentReviewReasons.None ? null : r.ReviewReasons.ToString();
                cells[3] = r.Title;
                for (var i = 0; i < fieldDefinitions.Count; i++)
                {
                    cells[systemCount + i] = GetExtractedValue(r, fieldDefinitions[i].Id, fieldDefinitions[i].DataType);
                }
                return cells;
            })
            .ToList();

        var bytes = ExportFileBuilder.Build(input.Format, headers, dataRows);

        var (extension, contentType) = input.Format switch
        {
            ExportFormat.Xlsx => (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => (".csv", "text/csv")
        };

        // #499 decision: {typeCode}-{timestamp}{ext}. TypeCode is constrained to [A-Za-z0-9_-.] by
        // DocumentTypeConsts.TypeCodePattern, so it is filename-safe; DisplayName is not (it is CJK).
        var fileName = documentType.TypeCode
            + "-" + Clock.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + extension;

        return new RemoteStreamContent(new MemoryStream(bytes), fileName, contentType);
    }

    private static string? GetExtractedValue(ExportProjection d, Guid fieldDefinitionId, FieldDataType dataType)
    {
        // Render all value rows for this field by Order ascending, then join (#212). Single-value fields have exactly one row,
        // so the result is that value. Multi-value fields join rows with "; ", preserving all values deterministically
        // without relying on DB row order for child subqueries without explicit ordering.
        var rendered = d.ExtractedFields
            .Where(f => f.FieldDefinitionId == fieldDefinitionId)
            .OrderBy(f => f.Order)
            .Select(f => FieldValueToString(f, dataType))
            .Where(s => s != null)
            .ToList();

        return rendered.Count > 0 ? string.Join("; ", rendered) : null;
    }

    // Render typed columns to cell strings by field type, using InvariantCulture and matching the canonical shape in DocumentExtractedField.ToJsonElement.
    // Type comes from FieldDefinition.DataType (#208: not persisted on field value rows). Unknown type loud-fails, consistent with
    // SetValue / ToJsonElement / ApplyFieldValueFilter. Never silently output an empty cell: if a new enum value misses this branch,
    // tests / runtime should fail loudly instead of silently exporting wrong data.
    private static string? FieldValueToString(ExtractedFieldProjection f, FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => f.TextValue,
        FieldDataType.LongText => f.LongTextValue,
        // Render Number in minimal shape ("0.######"): integer 1000 -> "1000", decimal 10.50 -> "10.5",
        // without the six trailing zeros from decimal(38,6).
        FieldDataType.Number => f.NumberValue?.ToString("0.######", CultureInfo.InvariantCulture),
        FieldDataType.Boolean => f.BooleanValue == null ? null : (f.BooleanValue.Value ? "true" : "false"),
        FieldDataType.Date => f.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldDataType.DateTime => f.DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };
}
