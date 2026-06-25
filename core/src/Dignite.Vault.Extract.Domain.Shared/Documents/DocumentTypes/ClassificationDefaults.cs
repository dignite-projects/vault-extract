namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// Default constants related to document classification. Centralized to avoid threshold magic values
/// scattered across classifiers.
/// </summary>
public static class ClassificationDefaults
{
    /// <summary>
    /// Default threshold for <c>DocumentType.ConfidenceThreshold</c>.
    /// Values below this, when the type is not otherwise whitelisted, enter the LowConfidence path.
    /// </summary>
    public const double DefaultConfidenceThreshold = 0.7;

    /// <summary>
    /// Fixed confidence written for manual classification.
    /// Note: the core Domain project does not depend on Abstractions, so
    /// <c>DocumentPipelineRunManager.CompleteManualClassificationAsync</c> uses literal 1.0. Keep that
    /// location synchronized if this constant changes.
    /// </summary>
    public const double ManualClassificationConfidence = 1.0;
}
