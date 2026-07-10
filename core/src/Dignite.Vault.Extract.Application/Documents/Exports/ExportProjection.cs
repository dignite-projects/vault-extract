using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export query projection: fetch only fields needed by export and <strong>exclude Markdown</strong>,
/// which can be a large OCR/body payload. When projecting to a non-entity type, EF automatically does
/// not SELECT unreferenced columns and does not track changes, avoiding loading Markdown into memory
/// for thousands of documents.
/// <para>
/// Fixed system fields (#207 / #287): <see cref="LifecycleStatus"/> /
/// <see cref="ReviewDisposition"/> / <see cref="ReviewReasons"/> / <see cref="Title"/> are emitted
/// by the export engine directly and are never configurable.
/// <see cref="ExtractedFields"/> is the typed projection of <see cref="DocumentExtractedField"/> child
/// rows, selected with the document through correlated subqueries / JOINs rather than per-document
/// N+1. Field columns match by <see cref="ExtractedFieldProjection.FieldDefinitionId"/> (#499: one column
/// per live <c>FieldDefinition</c> of the type, in <c>DisplayOrder</c>; the template layer is gone).
/// </para>
/// </summary>
internal sealed class ExportProjection
{
    public string? Title { get; init; }
    public DocumentLifecycleStatus LifecycleStatus { get; init; }
    public DocumentReviewDisposition ReviewDisposition { get; init; }

    /// <summary>Reason axis (#287). Documents with non-blocking MissingRequiredFields still enter type-bound export normally, but the export must expose the "missing required fields" quality signal because the disposition axis ReviewDisposition does not.</summary>
    public DocumentReviewReasons ReviewReasons { get; init; }

    public List<ExtractedFieldProjection> ExtractedFields { get; init; } = new();
}

/// <summary>Typed projection for one type-bound field value. Export renders the corresponding column cell string by field type (from FieldDefinition.DataType, #208) and matches field columns by <see cref="FieldDefinitionId"/> (#207).</summary>
internal sealed class ExtractedFieldProjection
{
    public Guid FieldDefinitionId { get; init; }

    /// <summary>Position of one row among multiple rows for a multi-value field (#212). Export renders <b>every</b> row for the field, ascending by this <c>Order</c>, joined with <c>"; "</c> — never relying on the unspecified DB row order of a child subquery. (An earlier revision of this comment claimed the smallest-Order row wins and that the join was "left for a later increment"; both were false — <c>DocumentExportAppService.GetExtractedValue</c> has always joined all rows.)</summary>
    public int Order { get; init; }

    public string? TextValue { get; init; }
    public string? LongTextValue { get; init; }
    public bool? BooleanValue { get; init; }
    public decimal? NumberValue { get; init; }
    public DateOnly? DateValue { get; init; }
    public DateTime? DateTimeValue { get; init; }
}
