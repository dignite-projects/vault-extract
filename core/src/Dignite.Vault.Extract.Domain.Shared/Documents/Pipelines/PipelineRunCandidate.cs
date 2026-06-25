namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// JSON payload schema for top-K candidates produced by the classification pipeline.
/// Physical storage location:
/// <see cref="DocumentPipelineRun.ExtraProperties"/>[<see cref="PipelineRunExtraPropertyNames.ClassificationCandidates"/>].
/// Exposed strongly typed to Angular through <see cref="DocumentPipelineRunDto.Candidates"/> so the
/// frontend does not cast by key string.
/// </summary>
public record PipelineRunCandidate(string TypeCode, double ConfidenceScore);
