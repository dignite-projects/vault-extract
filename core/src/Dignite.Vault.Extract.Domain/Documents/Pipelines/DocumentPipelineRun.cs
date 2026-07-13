using System;
using Dignite.Vault.Extract.Documents;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Document pipeline execution record.
/// One Document + PipelineCode + AttemptNumber uniquely identifies one execution.
/// The same pipeline can be retried; each retry creates a new record and increments AttemptNumber.
///
/// Implements <see cref="IHasExtraProperties"/>: pipeline outputs such as classification candidates
/// or chunk counts are written to ExtraProperties as key-value data, avoiding new columns for each
/// pipeline type.
/// <para>
/// Split in #216 from a <see cref="Document"/> aggregate child entity into an independent
/// <see cref="AggregateRoot{Guid}"/> operated through <see cref="IDocumentPipelineRunRepository"/>.
/// It is associated with <see cref="Document"/> by reference-by-id through <see cref="DocumentId"/>
/// with no navigation property, while the DB still keeps FK + CASCADE so hard-deleting Document also
/// removes runs. State transitions are still orchestrated by
/// <see cref="DocumentPipelineRunManager"/>. <c>internal</c> constructors and Mark methods keep direct
/// creation / status mutation inside the Domain assembly.
/// </para>
/// </summary>
public class DocumentPipelineRun : AggregateRoot<Guid>, IMultiTenant
{
    // ExtraProperties / IHasExtraProperties are provided by the AggregateRoot<TKey> base class and do
    // not need to be redeclared after the #216 split.

    public virtual Guid? TenantId { get; private set; }

    /// <summary>Owning document ID.</summary>
    public virtual Guid DocumentId { get; private set; }

    /// <summary>
    /// Pipeline identifier. Core constants are in <see cref="VaultExtractPipelines"/>.
    /// Business modules may register custom values; the recommended prefix is "{moduleCode}.".
    /// </summary>
    public virtual string PipelineCode { get; private set; } = default!;

    public virtual PipelineRunStatus Status { get; private set; }

    /// <summary>Attempt number, starting at 1 and incrementing on retry.</summary>
    public virtual int AttemptNumber { get; private set; }

    public virtual DateTime StartedAt { get; private set; }
    public virtual DateTime? CompletedAt { get; private set; }

    /// <summary>
    /// Status description. On failure this is the exception message; on skip this is the skip reason;
    /// on success it is usually null. Used only for diagnostics / audit display and carries no
    /// business semantics, which are expressed by fields on the relevant aggregate roots.
    /// </summary>
    public virtual string? StatusMessage { get; private set; }

    protected DocumentPipelineRun()
    {
        // AggregateRoot<TKey> base construction already initializes ExtraProperties +
        // SetDefaultsForExtraProperties.
    }

    internal DocumentPipelineRun(
        Guid id,
        Guid documentId,
        Guid? tenantId,
        string pipelineCode,
        int attemptNumber)
        : base(id)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        PipelineCode = pipelineCode;
        AttemptNumber = attemptNumber;
        Status = PipelineRunStatus.Pending;
    }

    internal void MarkRunning(DateTime now)
    {
        Status = PipelineRunStatus.Running;
        StartedAt = now;
        CompletedAt = null;
        // Every execution attempt starts from a clean slate. When ABP retries a failed job in place, the same run
        // row is re-begun (BeginOrStartAsync reuses it by the job's fixed PipelineRunId); without this reset a prior
        // attempt's failure message would survive into a subsequent Succeeded run and keep showing on the detail page.
        StatusMessage = null;
    }

    internal void MarkPending(DateTime now)
    {
        Status = PipelineRunStatus.Pending;
        StartedAt = now;
    }

    internal void MarkSucceeded(DateTime now)
    {
        Status = PipelineRunStatus.Succeeded;
        CompletedAt = now;
        // A succeeded run carries no diagnostic message (see StatusMessage doc). Defensive alongside the MarkRunning
        // reset: guarantees no stale failure text can co-exist with a Succeeded status even on any future path that
        // reaches success without re-entering MarkRunning.
        StatusMessage = null;
    }

    internal void MarkFailed(DateTime now, string statusMessage)
    {
        Status = PipelineRunStatus.Failed;
        StatusMessage = Truncate(statusMessage);
        CompletedAt = now;
    }

    internal void MarkSkipped(DateTime now, string statusMessage)
    {
        Status = PipelineRunStatus.Skipped;
        StatusMessage = Truncate(statusMessage);
        CompletedAt = now;
    }

    internal void PublishRunCompletedEvent()
    {
        AddLocalEvent(new DocumentPipelineRunCompletedEvent(DocumentId, PipelineCode, Status));
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= DocumentPipelineRunConsts.MaxStatusMessageLength
            ? value
            : value[..DocumentPipelineRunConsts.MaxStatusMessageLength];
}
