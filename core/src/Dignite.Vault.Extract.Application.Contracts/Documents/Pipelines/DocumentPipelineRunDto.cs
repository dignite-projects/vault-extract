using System;
using System.Collections.Generic;
using Dignite.Vault.Extract.Documents;
using Volo.Abp.ObjectExtending;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Common execution record for all pipelines. Pipeline-specific outputs are stored in
/// <see cref="ExtensibleObject.ExtraProperties"/>; keys are defined in
/// <see cref="PipelineRunExtraPropertyNames"/>. For client / Angular consumption, every explicitly
/// promoted key gets a strongly typed DTO property, currently only <see cref="Candidates"/>, so
/// abp generate-proxy propagates types to TypeScript and the frontend does not cast by string keys.
/// </summary>
public class DocumentPipelineRunDto : ExtensibleObject
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string PipelineCode { get; set; } = default!;
    public PipelineRunStatus Status { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Top-K candidate types returned by the classification pipeline LLM, written only on the
    /// low-confidence path (<c>DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync</c>).
    /// Physical storage:
    /// <see cref="ExtensibleObject.ExtraProperties"/>[<see cref="PipelineRunExtraPropertyNames.ClassificationCandidates"/>]
    /// as a JSON array. Server-side <c>DocumentPipelineRunToDocumentPipelineRunDtoMapper</c>
    /// deserializes from ExtraProperties into this property during mapping; downstream HTTP/STJ
    /// deserialization sets it directly. <see langword="null"/> means no candidates; empty array is
    /// not part of the contract.
    /// </summary>
    public IReadOnlyList<PipelineRunCandidate>? Candidates { get; set; }
}
