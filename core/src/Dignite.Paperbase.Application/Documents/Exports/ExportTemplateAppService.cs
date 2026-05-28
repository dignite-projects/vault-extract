using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents.Exports;

[Authorize]
public class ExportTemplateAppService : PaperbaseAppService, IExportTemplateAppService
{
    // 固定导出的系统字段表头（#207）——SourceType / LifecycleStatus / ReviewStatus / Title 始终输出，不走模板列配置。
    private static readonly IReadOnlyList<string> SystemFieldHeaders = new[]
    {
        "SourceType", "LifecycleStatus", "ReviewStatus", "Title"
    };

    private readonly IExportTemplateRepository _templateRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;

    public ExportTemplateAppService(
        IExportTemplateRepository templateRepository,
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository,
        IFieldDefinitionRepository fieldDefinitionRepository)
    {
        _templateRepository = templateRepository;
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
    }

    public virtual async Task<ExportTemplateDto> GetAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Templates.Default);
        var entity = await GetOwnedTemplateAsync(id);
        return await MapTemplateToDtoAsync(entity);
    }

    public virtual async Task<List<ExportTemplateDto>> GetListAsync()
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Templates.Default);
        var list = await _templateRepository.GetByTenantAsync();
        var dtos = ObjectMapper.Map<List<ExportTemplate>, List<ExportTemplateDto>>(list);
        await FillTemplateDtosAsync(list, dtos);
        return dtos;
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Create)]
    public virtual async Task<ExportTemplateDto> CreateAsync(CreateExportTemplateDto input)
    {
        await EnsureTemplateNameAvailableAsync(input.Name);
        var documentType = await ResolveDocumentTypeAsync(input.DocumentTypeCode);
        var columns = await MapColumnsAsync(input.Columns, documentType);

        var entity = new ExportTemplate(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.Name,
            input.Format,
            documentType.Id,
            columns);

        await _templateRepository.InsertAsync(entity, autoSave: true);
        return await MapTemplateToDtoAsync(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Update)]
    public virtual async Task<ExportTemplateDto> UpdateAsync(Guid id, UpdateExportTemplateDto input)
    {
        var entity = await GetOwnedTemplateAsync(id);

        // 仅在改名时判重——同名未变不必查（避免误判到自身）。
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await EnsureTemplateNameAvailableAsync(input.Name);
        }

        var documentType = await ResolveDocumentTypeAsync(input.DocumentTypeCode);
        var columns = await MapColumnsAsync(input.Columns, documentType);

        entity.Update(input.Name, input.Format, documentType.Id, columns);
        await _templateRepository.UpdateAsync(entity, autoSave: true);
        return await MapTemplateToDtoAsync(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await GetOwnedTemplateAsync(id);
        await _templateRepository.DeleteAsync(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Export)]
    public virtual async Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input)
    {
        var template = await GetOwnedTemplateAsync(input.TemplateId);

        // 租户隔离由 ambient IMultiTenant 过滤器施加（含下方 GetQueryableAsync）。
        var query = await _documentRepository.GetQueryableAsync();

        // 模板类型绑定（#207）：导出始终收窄到模板的 DocumentTypeId。
        query = query.Where(d => d.DocumentTypeId == template.DocumentTypeId);

        if (input.DocumentIds is { Count: > 0 } ids)
        {
            query = query.Where(d => ids.Contains(d.Id));
        }
        else if (input.LifecycleStatus.HasValue)
        {
            query = query.Where(d => d.LifecycleStatus == input.LifecycleStatus.Value);
        }

        // 单次 fetch (Max + 1) 投影到 ExportProjection（非实体类型 → 不 SELECT Markdown、不进 tracker）。
        // 多取 1 条用于原子判定超限——消除 count + Take 两次查询间并发插入导致的静默截断。
        // 会计场景漏导凭证比报错更危险，故超限 fail-fast。
        var limit = ExportTemplateConsts.MaxExportDocumentCount;
        var rows = await AsyncExecuter.ToListAsync(
            query
                .OrderByDescending(d => d.CreationTime)
                .Select(d => new ExportProjection
                {
                    SourceType = d.SourceType,
                    Title = d.Title,
                    LifecycleStatus = d.LifecycleStatus,
                    ReviewStatus = d.ReviewStatus,
                    // typed child 行随文档一并投影（单查询相关子查询，非逐文档 N+1）；按 FieldDefinitionId 匹配模板列。
                    ExtractedFields = d.ExtractedFieldValues
                        .Select(f => new ExtractedFieldProjection
                        {
                            FieldDefinitionId = f.FieldDefinitionId,
                            StringValue = f.StringValue,
                            BooleanValue = f.BooleanValue,
                            DecimalValue = f.DecimalValue,
                            DateValue = f.DateValue,
                            DateTimeValue = f.DateTimeValue,
                        })
                        .ToList(),
                })
                .Take(limit + 1));

        if (rows.Count > limit)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportDocumentLimitExceeded)
                .WithData("count", limit + "+")
                .WithData("max", limit);
        }

        // #208：字段类型不在字段值行持久化——按模板列 FieldDefinitionId 一次性 load FieldDefinition 拿 DataType，
        // 供 FieldValueToString 渲染 typed 列。穿透 soft-delete（列可能引用已归档字段）；有界（= 列数）。
        var columnFieldIds = template.Columns.Select(c => c.FieldDefinitionId).Distinct().ToList();
        var fieldDataTypes = new Dictionary<Guid, FieldDataType>();
        if (columnFieldIds.Count > 0)
        {
            using (DataFilter.Disable<ISoftDelete>())
            {
                foreach (var f in await _fieldDefinitionRepository.GetListAsync(f => columnFieldIds.Contains(f.Id)))
                {
                    fieldDataTypes[f.Id] = f.DataType;
                }
            }
        }

        // 固定系统字段列在前，模板配置的抽取字段列在后（#207）。
        var headers = new List<string>(SystemFieldHeaders);
        headers.AddRange(template.Columns.Select(c => c.ColumnName));

        var systemCount = SystemFieldHeaders.Count;
        var dataRows = rows
            .Select(r =>
            {
                var cells = new string?[headers.Count];
                cells[0] = r.SourceType.ToString();
                cells[1] = r.LifecycleStatus.ToString();
                cells[2] = r.ReviewStatus.ToString();
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

        // 跨层防御：只能访问自己所在层（Host admin 操作 TenantId IS NULL；租户 admin 操作自己租户）。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(ExportTemplate), id);
        }

        return entity;
    }

    protected virtual async Task EnsureTemplateNameAvailableAsync(string name)
    {
        var existing = await _templateRepository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateNameAlreadyExists)
                .WithData("Name", name);
        }
    }

    /// <summary>解析模板限定的文档类型（#207 必填）——按当前层 TypeCode 解析为实体；不存在则 loud fail。</summary>
    protected virtual async Task<DocumentType> ResolveDocumentTypeAsync(string documentTypeCode)
    {
        var type = await _documentTypeRepository.FindByTypeCodeAsync(documentTypeCode);
        if (type == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateInvalidDocumentTypeCode)
                .WithData("documentTypeCode", documentTypeCode);
        }

        return type;
    }

    /// <summary>把列入参的字段名解析为该类型下的 <c>FieldDefinitionId</c>（#207）；字段未定义则 loud fail。</summary>
    protected virtual async Task<List<ExportColumn>> MapColumnsAsync(
        IEnumerable<ExportColumnInput> columns, DocumentType documentType)
    {
        var result = new List<ExportColumn>();
        foreach (var c in columns)
        {
            var definition = await _fieldDefinitionRepository.FindByNameAsync(documentType.Id, c.FieldName);
            if (definition == null)
            {
                throw new BusinessException(PaperbaseErrorCodes.UnknownExtractedField)
                    .WithData("FieldName", c.FieldName)
                    .WithData("DocumentTypeCode", documentType.TypeCode);
            }

            result.Add(new ExportColumn(definition.Id, c.ColumnName, c.Order));
        }

        return result;
    }

    /// <summary>映射单个模板 → DTO，并填充 DocumentTypeCode + 每列 FieldName（Id → code/name，穿透 soft-delete）。</summary>
    protected virtual async Task<ExportTemplateDto> MapTemplateToDtoAsync(ExportTemplate template)
    {
        var dto = ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(template);
        await FillTemplateDtosAsync(new[] { template }, new[] { dto });
        return dto;
    }

    /// <summary>
    /// 批量填充模板 DTO 的 DocumentTypeCode + 列 FieldName（Id → code/name）。穿透 soft-delete——已归档类型 / 字段
    /// 也解析为"已归档"名而非空白；一次性解析两张映射表，无 N+1。
    /// </summary>
    protected virtual async Task FillTemplateDtosAsync(
        IReadOnlyList<ExportTemplate> templates, IReadOnlyList<ExportTemplateDto> dtos)
    {
        if (templates.Count == 0)
        {
            return;
        }

        var typeIds = templates.Select(t => t.DocumentTypeId).Distinct().ToList();
        var fieldIds = templates.SelectMany(t => t.Columns).Select(c => c.FieldDefinitionId).Distinct().ToList();

        var typeCodes = new Dictionary<Guid, string>();
        var fieldNames = new Dictionary<Guid, string>();
        using (DataFilter.Disable<ISoftDelete>())
        {
            foreach (var t in await _documentTypeRepository.GetListAsync(t => typeIds.Contains(t.Id)))
            {
                typeCodes[t.Id] = t.TypeCode;
            }

            if (fieldIds.Count > 0)
            {
                foreach (var f in await _fieldDefinitionRepository.GetListAsync(f => fieldIds.Contains(f.Id)))
                {
                    fieldNames[f.Id] = f.Name;
                }
            }
        }

        for (var i = 0; i < templates.Count; i++)
        {
            dtos[i].DocumentTypeCode = typeCodes.TryGetValue(templates[i].DocumentTypeId, out var code) ? code : null;
            foreach (var col in dtos[i].Columns)
            {
                col.FieldName = fieldNames.TryGetValue(col.FieldDefinitionId, out var name) ? name : null;
            }
        }
    }

    private static string? GetExtractedValue(
        ExportProjection d, Guid fieldDefinitionId, IReadOnlyDictionary<Guid, FieldDataType> fieldDataTypes)
    {
        var field = d.ExtractedFields.FirstOrDefault(f => f.FieldDefinitionId == fieldDefinitionId);
        if (field == null || !fieldDataTypes.TryGetValue(fieldDefinitionId, out var dataType))
        {
            return null;
        }

        return FieldValueToString(field, dataType);
    }

    // 按字段类型渲染类型化列为单元格字符串（InvariantCulture，与 DocumentExtractedField.ToJsonElement 的规范形一致）。
    // 类型来自 FieldDefinition.DataType（#208：不在字段值行持久化）。未知类型 loud fail——与 SetValue / ToJsonElement /
    // ApplyFieldValueFilter 一致（绝不静默吐空单元格：新增枚举值漏改本处应在测试 / 运行期响亮报错，而非无声错导）。
    private static string? FieldValueToString(ExtractedFieldProjection f, FieldDataType dataType) => dataType switch
    {
        FieldDataType.String => f.StringValue,
        // Number 以最小形渲染（"0.######"）：整数 1000 → "1000"，小数 10.50 → "10.5"——不带 decimal(38,6) 的 6 位尾零。
        FieldDataType.Number => f.DecimalValue?.ToString("0.######", CultureInfo.InvariantCulture),
        FieldDataType.Boolean => f.BooleanValue == null ? null : (f.BooleanValue.Value ? "true" : "false"),
        FieldDataType.Date => f.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldDataType.DateTime => f.DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };
}
