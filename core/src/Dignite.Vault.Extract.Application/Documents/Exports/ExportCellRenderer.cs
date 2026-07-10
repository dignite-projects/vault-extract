using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Renders one document's typed field-value rows into one export cell string.
/// <para>
/// Extracted from <c>DocumentExportAppService</c> so the ascending-<c>Order</c> join can actually be tested.
/// It could not be, in place: the only tests that could reach it went through SQLite, which returns
/// <c>DocumentExtractedField</c> child rows in primary-key order <c>(DocumentId, FieldDefinitionId, Order)</c> —
/// already ascending by <c>Order</c>. Deleting the sort left every test green. The row order of a child
/// subquery with no <c>ORDER BY</c> is unspecified, not "whatever SQLite does", so the guard belongs here, over
/// an input sequence a test controls.
/// </para>
/// </summary>
internal static class ExportCellRenderer
{
    /// <summary>
    /// Renders every value row of one field, ascending by <c>Order</c>, joined (#212). A single-value field has
    /// exactly one row, so the result is that value. Yields null — an empty cell — when the field holds no
    /// renderable value.
    /// </summary>
    public static string? RenderCell(IEnumerable<ExtractedFieldProjection> values, FieldDataType dataType)
    {
        var rendered = values
            // Never relies on the caller's sequence order: DocumentExportAppService buckets rows with ToLookup,
            // which preserves the source order, and that source order is the database's unspecified one.
            .OrderBy(f => f.Order)
            .Select(f => FieldValueToString(f, dataType))
            .Where(s => s != null)
            .ToList();

        return rendered.Count > 0 ? string.Join(MultiValueSeparator, rendered) : null;
    }

    /// <summary>
    /// Joins a multi-value field's rows in one cell. Not a comma: the CSV writer would then have to quote the
    /// cell, and a consumer re-splitting on the delimiter would silently shred one field into several columns.
    /// </summary>
    public const string MultiValueSeparator = "; ";

    // Render typed columns to cell strings by field type, using InvariantCulture and matching the canonical shape in DocumentExtractedField.ToJsonElement.
    // Type comes from FieldDefinition.DataType (#208: not persisted on field value rows). Unknown type loud-fails, consistent with
    // SetValue / ToJsonElement / ApplyFieldValueFilter. Never silently output an empty cell: if a new enum value misses this branch,
    // tests / runtime should fail loudly instead of silently exporting wrong data.
    private static string? FieldValueToString(ExtractedFieldProjection f, FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => f.TextValue,
        FieldDataType.LongText => f.LongTextValue,
        // Render Number in minimal shape ("0.######"): integer 1000 -> "1000", decimal 10.50 -> "10.5",
        // without the six trailing zeros from decimal(38,6).
        FieldDataType.Number => f.NumberValue?.ToString("0.######", CultureInfo.InvariantCulture),
        FieldDataType.Boolean => f.BooleanValue == null ? null : (f.BooleanValue.Value ? "true" : "false"),
        FieldDataType.Date => f.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldDataType.DateTime => f.DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };
}
