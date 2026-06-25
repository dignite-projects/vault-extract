using System;
using System.Collections.Generic;

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
}
