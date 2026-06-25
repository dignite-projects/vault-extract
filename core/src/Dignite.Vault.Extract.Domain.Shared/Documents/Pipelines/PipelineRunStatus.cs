namespace Dignite.Vault.Extract.Documents.Pipelines;

public enum PipelineRunStatus
{
    /// <summary>Created but not started yet (enqueued but not picked up).</summary>
    Pending = 10,

    /// <summary>Running.</summary>
    Running = 20,

    /// <summary>Completed successfully.</summary>
    Succeeded = 30,

    /// <summary>Failed; final state only after failures persist through the retry limit.</summary>
    Failed = 90,

    /// <summary>Skipped because prerequisites were not met or a tenant feature is disabled.</summary>
    Skipped = 95
}
