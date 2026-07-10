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

        // #501 item 2: the column count is bounded explicitly, symmetric with the row bound below. The template
        // layer capped its saved projection at ExportTemplateConsts.MaxColumnCount = 100; #499 deleted the
        // template and derived the columns from the type's live fields instead, and nothing else caps the number
        // of fields a type may carry. A very wide export is built synchronously and held in memory as one
        // ClosedXML cell object per (row, column) before SaveAs, so it fails fast here rather than degrading.
        if (fieldDefinitions.Count > DocumentExportConsts.MaxColumnCount)
        {
            throw new BusinessException(VaultExtractErrorCodes.Export.ColumnLimitExceeded)
                .WithData("count", fieldDefinitions.Count)
                .WithData("max", DocumentExportConsts.MaxColumnCount);
        }

        // Tenant isolation is enforced by the ambient IMultiTenant filter, including GetQueryableAsync below.
        var query = await _documentRepository.GetQueryableAsync();

        // #501 item 1: the metadata predicates are the shared DocumentQueries.ApplyMetadataFilter chain, the one
        // the operator list runs. Hand-writing a second copy here is how "download the current view" silently
        // stops downloading the view — the #496 bug class, one layer down. Filters the export contract does not
        // expose (ReviewDisposition) stay null; the recycle bin is fail-closed by never disabling ISoftDelete.
        query = query.ApplyMetadataFilter(new DocumentMetadataFilter
        {
            DocumentTypeId = documentType.Id,
            LifecycleStatus = input.LifecycleStatus,
            CabinetId = input.CabinetId,
            OriginDocumentId = input.OriginDocumentId,
            HasReviewReasons = input.HasReviewReasons,
            CreationTimeMin = input.CreationTimeMin,
            CreationTimeMax = input.CreationTimeMax,
        });

        // Extracted-field-value filters, AND-combined with the metadata filters above. Resolve the field names
        // against the type (unknown field loud-fails via the shared resolver — the same path the document list /
        // MCP search use), match document ids through the EXISTS query (GetFieldMatchedIdsAsync; the ambient
        // IMultiTenant filter keeps it in the caller's layer), then intersect. The Take(limit + 1) below still
        // bounds the export size. fieldDefinitions is passed as the already-loaded lookup (#501 item 4).
        if (input.FieldFilters is { Count: > 0 })
        {
            var fieldQueries = await DocumentFieldQueryResolver.ResolveAsync(
                _fieldDefinitionRepository, input.FieldFilters, documentType.Id, documentType.TypeCode,
                knownDefinitions: fieldDefinitions);
            var matchedIds = await _documentRepository.GetFieldMatchedIdsAsync(documentType.Id, fieldQueries);
            query = query.Where(d => matchedIds.Contains(d.Id));
        }

        // Single fetch of (Max + 1) projected to ExportProjection (non-entity type -> does not SELECT Markdown and does not enter tracker).
        // Fetch one extra row to atomically detect over limit, eliminating silent truncation caused by concurrent inserts between count + Take queries.
        // In accounting scenarios, missing exported vouchers is more dangerous than an error, so over limit fails fast.
        var limit = DocumentExportConsts.MaxExportDocumentCount;
        var rows = await AsyncExecuter.ToListAsync(
            query
                // The list's default order, from the one shared implementation (#501 item 5) — including the Id
                // tiebreaker, for the same reason the columns are ordered by (DisplayOrder, Name).
                .OrderByCreationTime(descending: true)
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
                // #501 item 3: bucket this row's field values by FieldDefinitionId once, in one pass. Each cell
                // is then an O(1) bucket lookup instead of a Where + OrderBy rescan of every value the document
                // holds — the old shape cost O(columns x values) per row, and #499 grew `columns` from an
                // operator-chosen subset capped at 100 to every live field the type declares.
                var valuesByField = r.ExtractedFields.ToLookup(f => f.FieldDefinitionId);

                var cells = new string?[headers.Count];
                cells[0] = r.LifecycleStatus.ToString();
                cells[1] = r.ReviewDisposition.ToString();
                // #287: reason axis. None -> empty cell (no unresolved reason); otherwise [Flags].ToString(), such as "MissingRequiredFields".
                // Empty vs non-empty is clearer than relying on "missing field cell left empty", which cannot distinguish optional empty from required missing.
                cells[2] = r.ReviewReasons == DocumentReviewReasons.None ? null : r.ReviewReasons.ToString();
                cells[3] = r.Title;
                for (var i = 0; i < fieldDefinitions.Count; i++)
                {
                    // The ILookup indexer yields an empty sequence for a field this document has no value for.
                    cells[systemCount + i] = ExportCellRenderer.RenderCell(
                        valuesByField[fieldDefinitions[i].Id], fieldDefinitions[i].DataType);
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
}
