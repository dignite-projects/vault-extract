namespace Dignite.Vault.Extract.Documents.Exports;

public static class DocumentExportConsts
{
    /// <summary>Hard limit for documents in one synchronous export, preventing broad filters from exhausting memory or generating oversized files. Hosts may override it.</summary>
    public static int MaxExportDocumentCount { get; set; } = 10000;

    /// <summary>
    /// Hard limit for type-bound field columns in one export — the row bound's missing twin (#501 item 2).
    /// <para>
    /// The deleted template layer capped its saved column projection at <c>ExportTemplateConsts.MaxColumnCount = 100</c>,
    /// enforced by <c>ExportTemplate.SetColumns</c>. #499 replaced that projection with the type's live
    /// <c>FieldDefinition</c> list, and nothing caps how many fields a document type may declare
    /// (<c>FieldDefinitionConsts</c> bounds name length and uniqueness, never the per-type count) — so the column
    /// count became unbounded while the row count stayed at 10 000. The export is built synchronously, holding
    /// one ClosedXML cell object per (row, column) in memory before <c>SaveAs</c>. Carries over the old value.
    /// Hosts may override it.
    /// </para>
    /// <para>
    /// The four fixed system columns are not counted: they are not configurable, so a host raising this limit is
    /// reasoning about the fields it declared, not about an implementation detail of the header row.
    /// </para>
    /// </summary>
    public static int MaxColumnCount { get; set; } = 100;
}
