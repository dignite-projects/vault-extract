namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Key constants for <see cref="DocumentPipelineRun.ExtraProperties"/>.
/// Each pipeline defines its own keys. Business modules that add pipelines should use the recommended
/// "{moduleCode}." prefix to avoid conflicts.
/// </summary>
public static class PipelineRunExtraPropertyNames
{
    /// <summary>
    /// Top-K candidate results from the classification pipeline.
    /// Written with <see cref="PipelineRunCandidate"/> as the JSON payload schema and exposed on read
    /// side through strongly typed <see cref="DocumentPipelineRunDto.Candidates"/>.
    /// <para>
    /// Must be <c>const</c>: this is the persisted key literal in the JSON column. Any runtime change
    /// would make historical <c>ExtraProperties["Candidates"]</c> unreadable.
    /// </para>
    /// </summary>
    public const string ClassificationCandidates = "Candidates";
}
