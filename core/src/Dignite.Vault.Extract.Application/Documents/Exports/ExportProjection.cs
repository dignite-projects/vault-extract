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
/// by the export engine directly and do not go through template-column configuration.
/// <see cref="ExtractedFields"/> is the typed projection of <see cref="DocumentExtractedField"/> child
/// rows, selected with the document through correlated subqueries / JOINs rather than per-document
/// N+1. Template columns match by <see cref="ExtractedFieldProjection.FieldDefinitionId"/>.
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

/// <summary>Typed projection for one type-bound field value. Export renders the corresponding column cell string by field type (from FieldDefinition.DataType, #208) and matches template columns by <see cref="FieldDefinitionId"/> (#207).</summary>
internal sealed class ExtractedFieldProjection
{
    public Guid FieldDefinitionId { get; init; }

    /// <summary>Position of one row among multiple rows for a multi-value field (#212). Export takes the smallest Order row when matching a column by <see cref="FieldDefinitionId"/>, matching the Order-0 scalar rendering in REST/MCP outbound surfaces and staying deterministic without relying on unspecified DB row order. Full multi-value join is left for a later increment.</summary>
    public int Order { get; init; }

    public string? TextValue { get; init; }
    public string? LongTextValue { get; init; }
    public bool? BooleanValue { get; init; }
    public decimal? NumberValue { get; init; }
    public DateOnly? DateValue { get; init; }
    public DateTime? DateTimeValue { get; init; }
}
