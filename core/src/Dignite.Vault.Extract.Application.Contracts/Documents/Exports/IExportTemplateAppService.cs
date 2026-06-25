using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export template management and execution, per tenant. Export is the channel's file output: field
/// projection and serialization only, with no business transformation.
/// </summary>
public interface IExportTemplateAppService : IApplicationService
{
    Task<ExportTemplateDto> GetAsync(Guid id);

    Task<List<ExportTemplateDto>> GetListAsync();

    Task<ExportTemplateDto> CreateAsync(CreateExportTemplateDto input);

    Task<ExportTemplateDto> UpdateAsync(Guid id, UpdateExportTemplateDto input);

    Task DeleteAsync(Guid id);

    /// <summary>Generates an export file from the template (CSV / XLSX) and returns the file stream synchronously.</summary>
    Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input);
}
