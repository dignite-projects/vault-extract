namespace Dignite.Vault.Extract.Documents;

public enum DocumentLifecycleStatus
{
    /// <summary>The file has been stored, and no pipeline has started yet.</summary>
    Uploaded = 10,

    /// <summary>At least one critical pipeline is still running or has not started yet.</summary>
    Processing = 20,

    /// <summary>
    /// All critical pipelines, Parse and Classification, completed successfully.
    /// The document is available to business workflows; non-critical pipelines such as embedding may
    /// still be running.
    /// </summary>
    Ready = 30,

    /// <summary>At least one critical pipeline has finally failed after retries were exhausted.</summary>
    Failed = 99
}
