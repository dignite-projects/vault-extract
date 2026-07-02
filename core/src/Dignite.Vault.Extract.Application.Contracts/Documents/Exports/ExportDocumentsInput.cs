using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export execution input. When <see cref="DocumentIds"/> is non-empty, exports by selected IDs and
/// ignores all filter fields; otherwise matches current-layer documents by filters, subject to the
/// per-export document count limit.
/// Document type is not specified here: templates are always type-bound
/// (<c>ExportTemplate.DocumentTypeId</c>), and exports are always narrowed to the template's type.
/// </summary>
public class ExportDocumentsInput
{
    public Guid TemplateId { get; set; }

    public List<Guid>? DocumentIds { get; set; }

    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    public Guid? CabinetId { get; set; }

    public DateTime? CreationTimeMin { get; set; }

    public DateTime? CreationTimeMax { get; set; }

    /// <summary>
    /// #414: extracted-field-value filters, resolved against the template's document type and AND-combined
    /// with the metadata filters above (reuses the same machinery as the document list / MCP search —
    /// <c>ApplyFieldValueFilter</c> via <c>GetFieldMatchedIdsAsync</c>). Ignored when <see cref="DocumentIds"/>
    /// is supplied. Each element self-validates; the list is capped like the list/search path.
    /// </summary>
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }
}
