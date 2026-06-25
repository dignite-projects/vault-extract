using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Exports;

public class CreateExportTemplateDto
{
    [Required]
    [DynamicStringLength(typeof(ExportTemplateConsts), nameof(ExportTemplateConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    /// <summary>Immutable id of the constrained document type (#207: required after converging to ExtractedField-only columns, because columns reference field definitions under this type).</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }

    [Required]
    [MinLength(1)]
    public List<ExportColumnInput> Columns { get; set; } = new();
}
