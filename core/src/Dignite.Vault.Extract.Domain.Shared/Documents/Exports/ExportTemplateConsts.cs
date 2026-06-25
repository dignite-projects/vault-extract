namespace Dignite.Vault.Extract.Documents.Exports;

public static class ExportTemplateConsts
{
    public static int MaxNameLength { get; set; } = 128;

    public static int MaxColumnCount { get; set; } = 100;

    /// <summary>Hard limit for documents in one synchronous export, preventing broad filters from exhausting memory or generating oversized files. Hosts may override it.</summary>
    public static int MaxExportDocumentCount { get; set; } = 10000;
}
