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

[Authorize]
public class ExportTemplateAppService : VaultExtractAppService, IExportTemplateAppService
{
    // Fixed exported system field headers (#207 / #287): LifecycleStatus / ReviewStatus (disposition axis) /
    // ReviewReasons (reason axis) / Title. Always exported and not configured through template columns.
    // ReviewStatus column name is stable, with DB column name also ReviewStatus, and value taken from ReviewDisposition.
    private static readonly IReadOnlyList<string> SystemFieldHeaders = new[]
    {
        "LifecycleStatus", "ReviewStatus", "ReviewReasons", "Title"
    };

    private readonly IExportTemplateRepository _templateRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly ExportTemplateManager _exportTemplateManager;

    public ExportTemplateAppService(
        IExportTemplateRepository templateRepository,
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository,
        ExportTemplateManager exportTemplateManager)
    {
        _templateRepository = templateRepository;
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
        _exportTemplateManager = exportTemplateManager;
    }

    public virtual async Task<ExportTemplateDto> GetAsync(Guid id)
    {
        await CheckPolicyAsync(VaultExtractPermissions.Documents.Templates.Default);
        var entity = await GetOwnedTemplateAsync(id);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    public virtual async Task<List<ExportTemplateDto>> GetListAsync()
    {
        await CheckPolicyAsync(VaultExtractPermissions.Documents.Templates.Default);
        // Tenant isolation is enforced by the ambient IMultiTenant filter; Name ASC ordering is kept in memory.
        var list = (await _templateRepository.GetListAsync())
            .OrderBy(t => t.Name)
            .ToList();
        return ObjectMapper.Map<List<ExportTemplate>, List<ExportTemplateDto>>(list);
    }

    [Authorize(VaultExtractPermissions.Documents.Templates.Create)]
    public virtual async Task<ExportTemplateDto> CreateAsync(CreateExportTemplateDto input)
    {
        await _exportTemplateManager.CheckNameAvailableAsync(input.Name);
        await EnsureDocumentTypeExistsAsync(input.DocumentTypeId);
        var columns = await MapColumnsAsync(input.Columns, input.DocumentTypeId);

        var entity = new ExportTemplate(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.Name,
            input.Format,
            input.DocumentTypeId,
            columns);

        await _templateRepository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    [Authorize(VaultExtractPermissions.Documents.Templates.Update)]
    public virtual async Task<ExportTemplateDto> UpdateAsync(Guid id, UpdateExportTemplateDto input)
    {
        var entity = await GetOwnedTemplateAsync(id);

        // Check duplicates only when renaming. If the name did not change, no lookup is needed and self-match false positives are avoided.
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await _exportTemplateManager.CheckNameAvailableAsync(input.Name);
        }

        await EnsureDocumentTypeExistsAsync(input.DocumentTypeId);
        var columns = await MapColumnsAsync(input.Columns, input.DocumentTypeId);

        entity.Update(input.Name, input.Format, input.DocumentTypeId, columns);
        await _templateRepository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    [Authorize(VaultExtractPermissions.Documents.Templates.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await GetOwnedTemplateAsync(id);
        await _templateRepository.DeleteAsync(entity);
    }

    [Authorize(VaultExtractPermissions.Documents.Export)]
    public virtual async Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input)
    {
        var template = await GetOwnedTemplateAsync(input.TemplateId);

        // Tenant isolation is enforced by the ambient IMultiTenant filter, including GetQueryableAsync below.
        var query = await _documentRepository.GetQueryableAsync();

        // Template type binding (#207): exports are always narrowed to the template's DocumentTypeId.
        query = query.Where(d => d.DocumentTypeId == template.DocumentTypeId);

        if (input.DocumentIds is { Count: > 0 } ids)
        {
            // Checked export: use the specified ID set and ignore all filter conditions.
            query = query.Where(d => ids.Contains(d.Id));
        }
        else
        {
            if (input.LifecycleStatus.HasValue)
                query = query.Where(d => d.LifecycleStatus == input.LifecycleStatus.Value);
            if (input.CabinetId.HasValue)
                query = query.Where(d => d.CabinetId == input.CabinetId.Value);

            // #496: the two filters the operator document list could express but the export could not, so that
            // "export current view" exports exactly the view. Both mirror DocumentAppService.ApplyFilter.
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

            // #414: extracted-field-value filters, AND-combined with the metadata filters above. Resolve the
            // field names against the template's type (unknown field loud-fails via the shared resolver — the
            // same path the document list / MCP search use), match document ids through the EXISTS query
            // (GetFieldMatchedIdsAsync; the ambient IMultiTenant filter keeps it in the caller's layer), then
            // intersect. The Take(limit + 1) below still bounds the export size.
            if (input.FieldFilters is { Count: > 0 })
            {
                var fieldQueries = await DocumentFieldQueryResolver.ResolveAsync(
                    _fieldDefinitionRepository,
                    input.FieldFilters,
                    template.DocumentTypeId,
                    await ResolveTypeCodeForErrorAsync(template.DocumentTypeId));
                var matchedIds = await _documentRepository.GetFieldMatchedIdsAsync(template.DocumentTypeId, fieldQueries);
                query = query.Where(d => matchedIds.Contains(d.Id));
            }
        }

        // Single fetch of (Max + 1) projected to ExportProjection (non-entity type -> does not SELECT Markdown and does not enter tracker).
        // Fetch one extra row to atomically detect over limit, eliminating silent truncation caused by concurrent inserts between count + Take queries.
        // In accounting scenarios, missing exported vouchers is more dangerous than an error, so over limit fails fast.
        var limit = ExportTemplateConsts.MaxExportDocumentCount;
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
                    // Match template columns by FieldDefinitionId.
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

        // #208: field type is not persisted on field value rows. Load FieldDefinition once by template column FieldDefinitionId
        // to get DataType for FieldValueToString rendering of typed columns. Traverse soft-delete because columns may reference archived fields.
        // Bounded by column count.
        var columnFieldIds = template.Columns.Select(c => c.FieldDefinitionId).Distinct().ToList();
        var fieldDataTypes = new Dictionary<Guid, FieldDataType>();
        var fieldDisplayNames = new Dictionary<Guid, string>();
        if (columnFieldIds.Count > 0)
        {
            using (DataFilter.Disable<ISoftDelete>())
            {
                foreach (var f in await _fieldDefinitionRepository.GetListAsync(f => columnFieldIds.Contains(f.Id)))
                {
                    fieldDataTypes[f.Id] = f.DataType;
                    fieldDisplayNames[f.Id] = f.DisplayName;
                }
            }
        }

        // Fixed system field columns first, then extracted field columns configured by the template (#207). Column headers use field DisplayName.
        var headers = new List<string>(SystemFieldHeaders);
        headers.AddRange(template.Columns.Select(c =>
            fieldDisplayNames.TryGetValue(c.FieldDefinitionId, out var displayName)
                ? displayName
                : c.FieldDefinitionId.ToString("N")));

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
                for (var i = 0; i < template.Columns.Count; i++)
                {
                    cells[systemCount + i] = GetExtractedValue(r, template.Columns[i].FieldDefinitionId, fieldDataTypes);
                }
                return cells;
            })
            .ToList();

        var bytes = ExportFileBuilder.Build(template.Format, headers, dataRows);

        var (fileName, contentType) = template.Format switch
        {
            ExportFormat.Xlsx => (template.Name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => (template.Name + ".csv", "text/csv")
        };

        return new RemoteStreamContent(new MemoryStream(bytes), fileName, contentType);
    }

    protected virtual async Task<ExportTemplate> GetOwnedTemplateAsync(Guid id)
    {
        var entity = await _templateRepository.GetAsync(id);

        // Cross-layer defense: callers may access only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(ExportTemplate), id);
        }

        return entity;
    }

    /// <summary>Asserts that the document type constrained by the template exists in the current layer (#207 required, associated by immutable Id); missing loud-fails.</summary>
    protected virtual async Task EnsureDocumentTypeExistsAsync(Guid documentTypeId)
    {
        var type = await _documentTypeRepository.FindAsync(documentTypeId);
        if (type == null)
        {
            throw new EntityNotFoundException(typeof(DocumentType), documentTypeId);
        }
    }

    /// <summary>
    /// Resolves the template's document-type code for a readable "unknown field" error message only (#414: the
    /// export is already scoped by <c>DocumentTypeId</c>). Soft-delete-traversed so a deleted type still yields
    /// its code; falls back to the id when the type row is gone.
    /// </summary>
    protected virtual async Task<string> ResolveTypeCodeForErrorAsync(Guid documentTypeId)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var type = await _documentTypeRepository.FindAsync(documentTypeId);
            return type?.TypeCode ?? documentTypeId.ToString();
        }
    }

    /// <summary>Validates that each column's <c>FieldDefinitionId</c> belongs to a field definition for this type in the current layer (#207); otherwise loud-fails.</summary>
    protected virtual async Task<List<ExportColumn>> MapColumnsAsync(
        IEnumerable<ExportColumnInput> columns, Guid documentTypeId)
    {
        var validFieldIds = (await _fieldDefinitionRepository.GetListAsync(documentTypeId))
            .Select(f => f.Id)
            .ToHashSet();

        var result = new List<ExportColumn>();
        foreach (var c in columns)
        {
            if (!validFieldIds.Contains(c.FieldDefinitionId))
            {
                throw new EntityNotFoundException(typeof(FieldDefinition), c.FieldDefinitionId);
            }

            result.Add(new ExportColumn(c.FieldDefinitionId, c.Order));
        }

        return result;
    }

    private static string? GetExtractedValue(
        ExportProjection d, Guid fieldDefinitionId, IReadOnlyDictionary<Guid, FieldDataType> fieldDataTypes)
    {
        if (!fieldDataTypes.TryGetValue(fieldDefinitionId, out var dataType))
        {
            return null;
        }

        // Render all value rows for this field by Order ascending, then join (#212). Single-value fields have exactly one row,
        // so the result is that value and matches existing behavior. Multi-value text fields join rows with "; ", preserving all values
        // deterministically without relying on DB row order for child subqueries without explicit ordering.
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
