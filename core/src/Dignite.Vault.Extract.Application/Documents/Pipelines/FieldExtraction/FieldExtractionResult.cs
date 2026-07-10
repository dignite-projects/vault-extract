namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Result of <see cref="FieldExtractionService.ExtractAsync"/>, used by callers (event handler /
/// background job) for observability logs. The extraction engine already handles ETO publication /
/// field persistence, so callers do not write to the DB based on this result.
/// </summary>
public enum FieldExtractionOutcome
{
    /// <summary>Prerequisite guard failed (missing document / cross-tenant / unclassified / stale / reclassified in flight); nothing written or published.</summary>
    Skipped,

    /// <summary>Target type has no field definitions; clears residual field rows and publishes an empty <c>FieldsExtractedEto</c>.</summary>
    Cleared,

    /// <summary>Normal extraction; writes the full field-value group and publishes <c>FieldsExtractedEto</c>.</summary>
    Extracted,

    /// <summary>
    /// The Markdown exceeded <c>VaultExtractBehaviorOptions.MaxFieldExtractionMarkdownLength</c> (#491), so no LLM call
    /// was made. Sets the blocking <c>DocumentReviewReasons.FieldExtractionIncomplete</c> signal, publishes nothing, and
    /// leaves any previously extracted values untouched. This is a <b>terminal, successful</b> run of the stage: the
    /// caller completes the pipeline run rather than throwing, so the job never re-enters the job-store retry loop with
    /// the same oversized body.
    /// </summary>
    Declined
}

public readonly record struct FieldExtractionResult(FieldExtractionOutcome Outcome, int FieldCount)
{
    public static readonly FieldExtractionResult Skipped = new(FieldExtractionOutcome.Skipped, 0);
    public static readonly FieldExtractionResult Cleared = new(FieldExtractionOutcome.Cleared, 0);
    public static readonly FieldExtractionResult Declined = new(FieldExtractionOutcome.Declined, 0);
    public static FieldExtractionResult Extracted(int fieldCount) => new(FieldExtractionOutcome.Extracted, fieldCount);
}
