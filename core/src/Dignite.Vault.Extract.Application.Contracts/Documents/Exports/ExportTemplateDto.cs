using System;
using System.Collections.Generic;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.Exports;

public class ExportTemplateDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = default!;
    public ExportFormat Format { get; set; }

    /// <summary>Immutable id of the constrained document type (#207: internal stable handle; TypeCode can be renamed by admins and is not used as a reference key).</summary>
    public Guid DocumentTypeId { get; set; }
    public List<ExportColumnDto> Columns { get; set; } = new();
}
