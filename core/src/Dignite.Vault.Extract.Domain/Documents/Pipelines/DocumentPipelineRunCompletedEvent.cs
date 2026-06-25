using System;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Local domain event emitted when a single pipeline run ends, whether it succeeds, fails, or is skipped.
/// Published inside <see cref="DocumentPipelineRunManager"/> CompleteAsync, FailAsync, and SkipAsync in
/// the same transaction as the status change.
/// Typical listeners include pipeline-level monitoring and auditing, retry decisions after failures, and
/// custom follow-up handling attached by business modules.
/// </summary>
public class DocumentPipelineRunCompletedEvent
{
    public Guid DocumentId { get; }
    public string PipelineCode { get; }
    public PipelineRunStatus Status { get; }

    public DocumentPipelineRunCompletedEvent(
        Guid documentId,
        string pipelineCode,
        PipelineRunStatus status)
    {
        DocumentId = documentId;
        PipelineCode = pipelineCode;
        Status = status;
    }
}
