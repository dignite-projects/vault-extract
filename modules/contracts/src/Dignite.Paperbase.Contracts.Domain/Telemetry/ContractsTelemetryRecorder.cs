using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Dignite.Paperbase.Abstractions.Agents;
using Dignite.Paperbase.Contracts.Contracts;
using Dignite.Paperbase.Contracts.EventHandlers;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Contracts.Telemetry;

/// <summary>
/// OpenTelemetry counters / histograms for the contract extraction pipeline (Issue #143).
/// Singleton lifetime matches the static <see cref="Meter"/> below — System.Diagnostics.Metrics
/// recommends one shared Meter per process.
///
/// <para>
/// <strong>Tag policy</strong>: low-cardinality enums only. <c>document_type_code</c> is
/// bounded by the registered <c>DocumentTypeDefinition</c> set so it's safe; <c>success</c>
/// is bool; <c>rule</c> is one of the constants on
/// <see cref="ContractExtractionValidator.RuleCodes"/>. <c>tenant_id</c> is deliberately
/// NOT a tag — per-tenant drill-down via traces / logs, not metrics.
/// </para>
///
/// <para>
/// Captured by the host's OpenTelemetry pipeline via the wildcard
/// <c>AddMeter("Dignite.Paperbase.*")</c> registration in
/// <c>PaperbaseHostModule.ConfigureOpenTelemetry</c> — no host-side change required.
/// </para>
/// </summary>
public class ContractsTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Paperbase.Contracts";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> ExtractionAttempts = Meter.CreateCounter<long>(
        "paperbase.contracts.extraction.attempts",
        description: "Contract field-extraction outcomes, by document type and final success. " +
                     "One increment per Document → Contract event, regardless of how many retries " +
                     "the agent middleware ran internally.");

    private static readonly Counter<long> ValidationErrors = Meter.CreateCounter<long>(
        "paperbase.contracts.extraction.validation_errors",
        description: "Per-rule validation failures observed on the final extraction result " +
                     "(after the retry middleware exhausted retries). Tagged by stable rule code " +
                     "from ContractExtractionValidator.RuleCodes.");

    private static readonly Histogram<double> ExtractionConfidence = Meter.CreateHistogram<double>(
        "paperbase.contracts.extraction.confidence",
        description: "Self-reported LLM confidence (0.0–1.0) of the final extraction. " +
                     "Drives the PendingReview routing threshold; useful as a leading indicator " +
                     "for prompt-quality regressions.");

    /// <summary>
    /// Records one extraction outcome. Called once per <c>DocumentClassifiedEto</c> handled,
    /// after the retry middleware has produced its final result and the validator has been
    /// re-run on that result for the metric snapshot.
    /// </summary>
    /// <param name="documentTypeCode">
    /// Document type that triggered the extraction (low-cardinality — bounded by the registered
    /// <c>DocumentTypeDefinition</c> set). Empty string for unknown types so the dashboard
    /// tag never carries null.
    /// </param>
    /// <param name="finalValidation">
    /// Validation result of the final extraction. Drives both <c>attempts{success}</c>
    /// and per-rule <c>validation_errors</c> counters.
    /// </param>
    /// <param name="finalConfidence">
    /// LLM-reported confidence of the final extraction. <c>null</c> means the LLM couldn't
    /// estimate — not recorded as a histogram sample so percentiles stay clean.
    /// </param>
    public virtual void RecordExtraction(
        string documentTypeCode,
        ExtractionValidationResult finalValidation,
        double? finalConfidence)
    {
        var typeTag = string.IsNullOrEmpty(documentTypeCode) ? "(unknown)" : documentTypeCode;

        ExtractionAttempts.Add(1,
            new KeyValuePair<string, object?>("document_type_code", typeTag),
            new KeyValuePair<string, object?>("success", finalValidation.IsValid));

        foreach (var violation in finalValidation.Errors)
        {
            ValidationErrors.Add(1,
                new KeyValuePair<string, object?>("rule", violation.RuleCode),
                new KeyValuePair<string, object?>("document_type_code", typeTag));
        }

        if (finalConfidence.HasValue)
        {
            ExtractionConfidence.Record(finalConfidence.Value,
                new KeyValuePair<string, object?>("document_type_code", typeTag),
                new KeyValuePair<string, object?>("success", finalValidation.IsValid));
        }
    }
}
