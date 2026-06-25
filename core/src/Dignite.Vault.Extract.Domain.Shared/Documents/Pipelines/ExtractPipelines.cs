using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Pipeline identifier constants defined by the core layer.
/// Business modules may register custom PipelineCode values, with "{moduleCode}." as the recommended
/// naming prefix, but they are not included in lifecycle derivation.
/// <para>
/// <see cref="Parse"/> / <see cref="Classification"/> must be <c>const</c>: they are
/// persisted to the <c>DocumentPipelineRun.PipelineCode</c> column, passed across JobArgs / ETO
/// payloads, and used as constant patterns in the <c>DocumentPipelineJobScheduler</c> switch
/// expression. Any runtime mutation would make historical DB data write under the old code while new
/// code reads under the new code, breaking dispatch logic.
/// </para>
/// </summary>
public static class ExtractPipelines
{
    /// <summary>Text extraction (OCR or native extraction). Key pipeline.</summary>
    public const string Parse = "text-extraction";

    /// <summary>Document classification (rule matching / AI). Key pipeline.</summary>
    public const string Classification = "classification";

    /// <summary>
    /// Type-bound field extraction (#289). <b>Key pipeline</b> since #411: participates in the Ready gate
    /// (<c>DocumentPipelineRunManager.DeriveLifecycleAsync</c>), so <c>DocumentReadyEto</c> is withheld until field
    /// extraction succeeds — this is what lets the duplicate check (which needs extracted fields to compute
    /// <c>Document.FieldFingerprint</c>) gate Ready before downstream consumes a suspected duplicate. The
    /// classification-completed cascade (<c>FieldExtractionEventHandler</c>) therefore enqueues
    /// <c>DocumentFieldExtractionBackgroundJob</c> so the cascade path also creates a <c>DocumentPipelineRun</c>;
    /// the same pipeline is the independent trigger for on-demand / bulk field re-extraction.
    /// <para>
    /// <b>Containers run no field extraction</b> and are exempted from this requirement in
    /// <c>DeriveLifecycleAsync</c>. <b>Consequence (#411):</b> bulk re-extraction (#289) of an already-Ready
    /// document now bounces <c>Ready → Processing → Ready</c> and re-fires <c>DocumentReadyEto</c>; downstream
    /// absorbs the re-delivery via the at-least-once + <c>EventTime</c> idempotency contract. This deliberately
    /// reverses the former lifecycle-neutral property.
    /// </para>
    /// </summary>
    public const string FieldExtraction = "field-extraction";

    /// <summary>
    /// Pipelines considered "key" during lifecycle derivation. Since #411 <see cref="FieldExtraction"/> is included
    /// so the duplicate check can gate Ready; containers are exempted from the field-extraction requirement in
    /// <c>DeriveLifecycleAsync</c> because they run no field extraction.
    /// </summary>
    public static readonly IReadOnlyCollection<string> KeyPipelines = new[]
    {
        Parse,
        Classification,
        FieldExtraction
    };

    /// <summary>
    /// Pipelines users can manually retry.
    /// Custom pipelines from business modules are not exposed for retry through this API.
    /// </summary>
    public static readonly IReadOnlyCollection<string> RetryablePipelines = new[]
    {
        Parse,
        Classification,
        FieldExtraction
    };
}
