using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #123: structured telemetry for L2/L3 RelationDiscovery — counters / histograms exported via
/// <see cref="System.Diagnostics.Metrics"/> and a paired structured log line per metric event.
///
/// <para>
/// <strong>Why a project-specific recorder vs ABP audit log</strong>: BackgroundJob runs are not in
/// HTTP context and have no audit scope by default; we need metrics, not audit rows. The
/// log lines are kept (not replaced) to preserve developer-grade observability.
/// </para>
///
/// <para>
/// <strong>Tag policy</strong>: tags are low-cardinality enums or buckets. <c>tenant_id</c> is
/// intentionally NOT a tag (would cause cardinality explosion in multi-tenant deployments
/// and isn't needed for ops dashboards — per-tenant drill-down via traces / logs instead).
/// </para>
/// </summary>
public class RelationDiscoveryTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Paperbase.Documents.RelationDiscovery";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> RunsTotal = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.runs.total",
        description: "RelationDiscovery background job executions, by result (succeeded / failed / document_missing).");

    private static readonly Histogram<long> L2Created = Meter.CreateHistogram<long>(
        "paperbase.relation_discovery.l2.created",
        description: "AiSuggested DocumentRelations created by L2 (structured fan-out) per run.");

    private static readonly Counter<long> L3Invoked = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.l3.invoked",
        description: "Times L3 (semantic + LLM fallback) was invoked because L2 found zero peers.");

    private static readonly Counter<long> L3LlmCalls = Meter.CreateCounter<long>(
        "paperbase.relation_discovery.l3.llm_calls",
        description: "Per-candidate LLM evaluations by L3, by result (confirmed / rejected / error).");

    private static readonly Histogram<long> L3Created = Meter.CreateHistogram<long>(
        "paperbase.relation_discovery.l3.created",
        description: "AiSuggested DocumentRelations created by L3 per run (only when L3 invoked).");

    private static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "paperbase.relation_discovery.duration",
        unit: "ms",
        description: "Wall-clock duration per RelationDiscovery layer (l2 / l3 / total).");

    /// <summary>
    /// AiSuggested → Manual conversions. The ONLY ground-truth signal for L2/L3 quality —
    /// the model's self-reported <see cref="DocumentRelation.Confidence"/> is not ground truth.
    /// </summary>
    private static readonly Counter<long> SuggestionConfirmed = Meter.CreateCounter<long>(
        "paperbase.relation.suggestion.confirmed",
        description: "User accepted an AiSuggested DocumentRelation (Confirm). Tags: source, confidence_bucket.");

    /// <summary>
    /// AiSuggested deletions. Paired with <see cref="SuggestionConfirmed"/> for the accept-rate funnel:
    /// <c>accept_rate = confirmed / (confirmed + rejected)</c>.
    /// </summary>
    private static readonly Counter<long> SuggestionRejected = Meter.CreateCounter<long>(
        "paperbase.relation.suggestion.rejected",
        description: "User deleted an AiSuggested DocumentRelation (Delete). Tags: source, confidence_bucket.");

    private readonly ILogger<RelationDiscoveryTelemetryRecorder> _logger;

    public RelationDiscoveryTelemetryRecorder(ILogger<RelationDiscoveryTelemetryRecorder> logger)
    {
        _logger = logger;
    }

    public virtual void RecordRun(RelationDiscoveryRunMetrics metrics)
    {
        RunsTotal.Add(1, new KeyValuePair<string, object?>("result", metrics.Result.ToString()));

        if (metrics.L2CreatedCount.HasValue)
        {
            L2Created.Record(metrics.L2CreatedCount.Value);
        }

        if (metrics.L3Invoked)
        {
            L3Invoked.Add(1);
            if (metrics.L3CreatedCount.HasValue)
            {
                L3Created.Record(metrics.L3CreatedCount.Value);
            }
        }

        if (metrics.L2DurationMs.HasValue)
        {
            RunDuration.Record(metrics.L2DurationMs.Value, new KeyValuePair<string, object?>("layer", "l2"));
        }
        if (metrics.L3DurationMs.HasValue)
        {
            RunDuration.Record(metrics.L3DurationMs.Value, new KeyValuePair<string, object?>("layer", "l3"));
        }
        if (metrics.TotalDurationMs.HasValue)
        {
            RunDuration.Record(metrics.TotalDurationMs.Value, new KeyValuePair<string, object?>("layer", "total"));
        }

        if (metrics.Result == RelationDiscoveryRunResult.Succeeded)
        {
            _logger.LogInformation(
                "RelationDiscovery run succeeded. DocumentId={DocumentId} L2Created={L2Created} L3Invoked={L3Invoked} L3Created={L3Created} L2Ms={L2Ms} L3Ms={L3Ms} TotalMs={TotalMs}",
                metrics.DocumentId,
                metrics.L2CreatedCount,
                metrics.L3Invoked,
                metrics.L3CreatedCount,
                metrics.L2DurationMs,
                metrics.L3DurationMs,
                metrics.TotalDurationMs);
        }
        else
        {
            _logger.LogWarning(
                "RelationDiscovery run did not complete normally. DocumentId={DocumentId} Result={Result} FailureReason={FailureReason} TotalMs={TotalMs}",
                metrics.DocumentId,
                metrics.Result,
                metrics.FailureReason,
                metrics.TotalDurationMs);
        }
    }

    public virtual void RecordL3LlmCall(RelationDiscoveryL3CallResult result)
    {
        L3LlmCalls.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
    }

    public virtual void RecordSuggestionConfirmed(RelationSource originalSource, double? confidence)
    {
        SuggestionConfirmed.Add(1,
            new KeyValuePair<string, object?>("source", originalSource.ToString()),
            new KeyValuePair<string, object?>("confidence_bucket", BucketConfidence(confidence)));
    }

    public virtual void RecordSuggestionRejected(RelationSource originalSource, double? confidence)
    {
        SuggestionRejected.Add(1,
            new KeyValuePair<string, object?>("source", originalSource.ToString()),
            new KeyValuePair<string, object?>("confidence_bucket", BucketConfidence(confidence)));
    }

    /// <summary>
    /// Bucket continuous confidence into 4 fixed buckets aligned with
    /// <see cref="Ai.PaperbaseAIBehaviorOptions.SemanticRelationDiscoveryConfidenceThreshold"/> = 0.7
    /// (anything below threshold should never reach storage, so &lt;0.7 is a "shouldn't happen" signal).
    /// </summary>
    protected virtual string BucketConfidence(double? confidence)
    {
        if (!confidence.HasValue) return "(none)";   // Manual relations have null confidence
        if (confidence.Value < 0.7) return "<0.7";
        if (confidence.Value < 0.8) return "0.7-0.8";
        if (confidence.Value < 0.9) return "0.8-0.9";
        return "0.9+";
    }
}

/// <summary>Per-run metrics emitted by <see cref="RelationDiscoveryBackgroundJob"/> at completion.</summary>
public sealed record RelationDiscoveryRunMetrics
{
    public required Guid DocumentId { get; init; }
    public required RelationDiscoveryRunResult Result { get; init; }

    /// <summary>Number of AiSuggested relations L2 created (null = L2 didn't run, e.g. document missing).</summary>
    public int? L2CreatedCount { get; init; }

    /// <summary>True when L2 returned 0 and L3 was invoked.</summary>
    public bool L3Invoked { get; init; }

    /// <summary>Number of AiSuggested relations L3 created (null = L3 didn't run).</summary>
    public int? L3CreatedCount { get; init; }

    public double? L2DurationMs { get; init; }
    public double? L3DurationMs { get; init; }
    public double? TotalDurationMs { get; init; }

    /// <summary>Set when <see cref="Result"/> is <see cref="RelationDiscoveryRunResult.Failed"/>.</summary>
    public string? FailureReason { get; init; }
}

public enum RelationDiscoveryRunResult
{
    Succeeded = 0,
    Failed = 1,
    DocumentMissing = 2
}

/// <summary>Per-candidate LLM evaluation result inside L3.</summary>
public enum RelationDiscoveryL3CallResult
{
    /// <summary>LLM returned <c>IsRelated=true</c> with confidence ≥ threshold; relation created.</summary>
    Confirmed = 0,

    /// <summary>LLM returned <c>IsRelated=false</c> OR confidence below threshold; no relation created.</summary>
    Rejected = 1,

    /// <summary>LLM call threw — candidate dropped, others continue (per-candidate isolation).</summary>
    Error = 2
}
