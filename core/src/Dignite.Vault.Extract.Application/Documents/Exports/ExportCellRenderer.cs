using System.Collections.Generic;
using System.Linq;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Joins one document's typed field-value rows into one export cell string. The per-<c>FieldDataType</c>
/// rendering itself belongs to <see cref="FieldValueFormatter"/> (#501 item 8); what lives here is the
/// multi-value join, which only the export performs.
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
    /// Joins a multi-value field's rows in one cell.
    /// <para>
    /// #501 item 6: the screen joins with this too (<c>formatExtractedFieldValue</c> in
    /// <c>angular/…/shared/format-field-value.ts</c>). It cannot be a comma. A comma is the CSV delimiter, so the
    /// writer would have to quote the cell, and a consumer re-splitting a quoted cell on commas shreds one field
    /// across several columns. The screen moved to the file's separator rather than the reverse, because the file
    /// already has consumers parsing it and its bytes are the riskier thing to change.
    /// </para>
    /// </summary>
    public const string MultiValueSeparator = "; ";

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
            .Select(f => FieldValueFormatter.ToCellString(f, dataType))
            .Where(s => s != null)
            .ToList();

        return rendered.Count > 0 ? string.Join(MultiValueSeparator, rendered) : null;
    }
}
