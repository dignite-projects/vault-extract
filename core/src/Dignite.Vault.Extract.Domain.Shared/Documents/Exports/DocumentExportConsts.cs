namespace Dignite.Vault.Extract.Documents.Exports;

public static class DocumentExportConsts
{
    /// <summary>Hard limit for documents in one synchronous export, preventing broad filters from exhausting memory or generating oversized files. Hosts may override it.</summary>
    public static int MaxExportDocumentCount { get; set; } = 10000;
}
