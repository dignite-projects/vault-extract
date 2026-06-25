namespace Dignite.Vault.Extract.Documents.Exports;

/// <summary>
/// Export template output format. Only CSV and XLSX are supported; JSON file export has limited value
/// because programmatic consumers can already get JSON through REST.
/// </summary>
public enum ExportFormat
{
    Csv = 0,
    Xlsx = 1
}
