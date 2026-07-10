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
    /// #496: operator review-queue filter, the export twin of <see cref="GetDocumentListInput.HasReviewReasons"/>
    /// (#284 / #395). <c>true</c> exports only documents that still require operator attention, evaluated by the
    /// canonical <c>DocumentReviewQueries.RequiresAttention</c> predicate the document list and the needs-review
    /// badge already run — never a second, hand-rolled copy. Ignored when <see cref="DocumentIds"/> is supplied.
    /// </summary>
    public bool? HasReviewReasons { get; set; }

    /// <summary>
    /// #496: provenance filter, the export twin of <see cref="GetDocumentListInput.OriginDocumentId"/> (#354).
    /// When set, exports only the sub-documents derived from this source document (those whose
    /// <c>Document.OriginDocumentId</c> equals it). Ignored when <see cref="DocumentIds"/> is supplied. Stays
    /// under the ABP <c>IMultiTenant</c> global filter, so it can only ever reach the caller's own layer.
    /// </summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>
    /// #414: extracted-field-value filters, resolved against the template's document type and AND-combined
    /// with the metadata filters above (reuses the same machinery as the document list / MCP search —
    /// <c>ApplyFieldValueFilter</c> via <c>GetFieldMatchedIdsAsync</c>). Ignored when <see cref="DocumentIds"/>
    /// is supplied. Each element self-validates; the list is capped like the list/search path.
    /// </summary>
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }
}
