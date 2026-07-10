using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export execution input (#499). Matches current-layer documents of <see cref="DocumentTypeCode"/> by the same
/// filters the operator document list composes, and emits the four fixed system columns followed by <b>every live
/// field defined on that type</b>, ordered by <c>FieldDefinition.DisplayOrder</c>. Subject to the per-export
/// document count limit, which fails fast rather than truncating.
/// <para>
/// There is no saved column projection and no explicit id set. #499 deleted the export-template layer because a
/// template's only persisted content was "which of this type's fields, in what order" — and ordering is already
/// owned by <c>DisplayOrder</c>, the same axis the list renders its columns by. One axis, so the file cannot
/// disagree with the screen. The old <c>DocumentIds</c> branch went with it: it was unreachable from the only UI
/// (<c>abp-extensible-table</c> has no row selection) and its "ignores every filter" semantics was exactly the
/// screen/file divergence #496 set out to remove.
/// </para>
/// </summary>
public class ExportDocumentsInput
{
    /// <summary>
    /// Required: extracted fields are type-scoped, so an export must name exactly one type. Mirrors the existing
    /// rule that <see cref="FieldFilters"/> requires a type on the list / MCP search path. A mixed-type view has
    /// no field columns and cannot be exported.
    /// </summary>
    [Required]
    [DynamicStringLength(typeof(DocumentTypeConsts), nameof(DocumentTypeConsts.MaxTypeCodeLength))]
    public string DocumentTypeCode { get; set; } = default!;

    /// <summary>
    /// <c>EnumDataType</c> because System.Text.Json casts any JSON number straight onto the enum without a range
    /// check, and both format switches (here and in <c>ExportFileBuilder</c>) fall through to CSV. Without this,
    /// <c>{"format": 99}</c> would return 200 + a CSV instead of 400 — a caller asking for a format we do not have
    /// would be told it succeeded.
    /// </summary>
    [EnumDataType(typeof(ExportFormat))]
    public ExportFormat Format { get; set; } = ExportFormat.Csv;

    public DocumentLifecycleStatus? LifecycleStatus { get; set; }

    public Guid? CabinetId { get; set; }

    public DateTime? CreationTimeMin { get; set; }

    public DateTime? CreationTimeMax { get; set; }

    /// <summary>
    /// Operator review-queue filter (#284 / #395), evaluated by the canonical
    /// <c>DocumentReviewQueries.RequiresAttention</c> predicate the document list and the needs-review badge
    /// already run — never a second, hand-rolled copy.
    /// </summary>
    public bool? HasReviewReasons { get; set; }

    /// <summary>
    /// Provenance filter (#354): when set, exports only the sub-documents derived from this source document.
    /// Stays under the ABP <c>IMultiTenant</c> global filter, so it can only ever reach the caller's own layer.
    /// </summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>
    /// Extracted-field-value filters, resolved against <see cref="DocumentTypeCode"/> and AND-combined with the
    /// metadata filters above through the same machinery as the document list / MCP search
    /// (<c>DocumentFieldQueryResolver</c> → <c>GetFieldMatchedIdsAsync</c>). Each element self-validates; the
    /// list is capped like the list/search path.
    /// </summary>
    [MaxLength(DocumentConsts.MaxSearchFieldFilters)]
    public List<DocumentFieldFilter>? FieldFilters { get; set; }
}
