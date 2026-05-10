using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Direct tests against <see cref="RelationDiscoveryTelemetryRecorder"/> using a
/// <see cref="MeterListener"/> to capture emitted measurements. Meter is process-wide
/// (singleton), so listener filters by instrument name to avoid noise from other tests.
/// </summary>
public class RelationDiscoveryTelemetryRecorder_Tests
{
    private readonly RelationDiscoveryTelemetryRecorder _recorder = new(NullLogger<RelationDiscoveryTelemetryRecorder>.Instance);

    [Fact]
    public void RecordRun_Should_Emit_Counter_With_Result_Tag()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.runs.total");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            L2CreatedCount = 0,
            TotalDurationMs = 12.5
        });

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(1L);
        capture.Measurements[0].Tags["result"].ShouldBe("Succeeded");
    }

    [Fact]
    public void RecordRun_Should_Record_L2_Created_Histogram_When_Set()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.l2.created");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            L2CreatedCount = 3
        });

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Value.ShouldBe(3L);
    }

    [Fact]
    public void RecordRun_Should_Skip_L3_Histogram_When_L3_Not_Invoked()
    {
        using var l3InvokedCapture = new MeterCapture("paperbase.relation_discovery.l3.invoked");
        using var l3CreatedCapture = new MeterCapture("paperbase.relation_discovery.l3.created");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            L2CreatedCount = 1,
            L3Invoked = false
        });

        l3InvokedCapture.Measurements.ShouldBeEmpty();
        l3CreatedCapture.Measurements.ShouldBeEmpty();
    }

    [Fact]
    public void RecordRun_Should_Emit_L3_Counter_And_Histogram_When_L3_Invoked()
    {
        using var l3InvokedCapture = new MeterCapture("paperbase.relation_discovery.l3.invoked");
        using var l3CreatedCapture = new MeterCapture("paperbase.relation_discovery.l3.created");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            L2CreatedCount = 0,
            L3Invoked = true,
            L3CreatedCount = 2
        });

        l3InvokedCapture.Measurements.Count.ShouldBe(1);
        l3InvokedCapture.Measurements[0].Value.ShouldBe(1L);
        l3CreatedCapture.Measurements.Count.ShouldBe(1);
        l3CreatedCapture.Measurements[0].Value.ShouldBe(2L);
    }

    [Fact]
    public void RecordRun_Should_Emit_Duration_Histogram_With_Layer_Tags()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.duration");

        _recorder.RecordRun(new RelationDiscoveryRunMetrics
        {
            DocumentId = Guid.NewGuid(),
            Result = RelationDiscoveryRunResult.Succeeded,
            L2DurationMs = 5.0,
            L3DurationMs = 1500.0,
            TotalDurationMs = 1505.0,
            L3Invoked = true
        });

        capture.Measurements.Count.ShouldBe(3);
        capture.Measurements.ShouldContain(m => (string)m.Tags["layer"] == "l2" && Math.Abs(m.ValueAsDouble - 5.0) < 0.01);
        capture.Measurements.ShouldContain(m => (string)m.Tags["layer"] == "l3" && Math.Abs(m.ValueAsDouble - 1500.0) < 0.01);
        capture.Measurements.ShouldContain(m => (string)m.Tags["layer"] == "total" && Math.Abs(m.ValueAsDouble - 1505.0) < 0.01);
    }

    [Fact]
    public void RecordL3LlmCall_Should_Tag_With_Result()
    {
        using var capture = new MeterCapture("paperbase.relation_discovery.l3.llm_calls");

        _recorder.RecordL3LlmCall(RelationDiscoveryL3CallResult.Confirmed);
        _recorder.RecordL3LlmCall(RelationDiscoveryL3CallResult.Rejected);
        _recorder.RecordL3LlmCall(RelationDiscoveryL3CallResult.Error);

        capture.Measurements.Count.ShouldBe(3);
        capture.Measurements.Select(m => (string)m.Tags["result"])
            .ShouldBe(new[] { "Confirmed", "Rejected", "Error" });
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData(0.5, "<0.7")]
    [InlineData(0.7, "0.7-0.8")]
    [InlineData(0.79, "0.7-0.8")]
    [InlineData(0.8, "0.8-0.9")]
    [InlineData(0.89, "0.8-0.9")]
    [InlineData(0.9, "0.9+")]
    [InlineData(0.95, "0.9+")]
    [InlineData(1.0, "0.9+")]
    public void RecordSuggestionConfirmed_Should_Bucket_Confidence(double? confidence, string expectedBucket)
    {
        using var capture = new MeterCapture("paperbase.relation.suggestion.confirmed");

        _recorder.RecordSuggestionConfirmed(RelationSource.AiSuggested, confidence);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Tags["confidence_bucket"].ShouldBe(expectedBucket);
        capture.Measurements[0].Tags["source"].ShouldBe("AiSuggested");
    }

    [Fact]
    public void RecordSuggestionRejected_Should_Tag_Source_And_Confidence_Bucket()
    {
        using var capture = new MeterCapture("paperbase.relation.suggestion.rejected");

        _recorder.RecordSuggestionRejected(RelationSource.AiSuggested, 0.85);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Tags["source"].ShouldBe("AiSuggested");
        capture.Measurements[0].Tags["confidence_bucket"].ShouldBe("0.8-0.9");
    }

    [Fact]
    public void RecordSuggestionConfirmed_Should_Handle_Null_Confidence_For_Manual_Source()
    {
        // Manual relations carry null Confidence by domain invariant. Recorder must accept this.
        using var capture = new MeterCapture("paperbase.relation.suggestion.confirmed");

        _recorder.RecordSuggestionConfirmed(RelationSource.Manual, confidence: null);

        capture.Measurements.Count.ShouldBe(1);
        capture.Measurements[0].Tags["source"].ShouldBe("Manual");
        capture.Measurements[0].Tags["confidence_bucket"].ShouldBe("(none)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposable scope that captures all measurements emitted to a single instrument
    /// on the RelationDiscovery meter. Filters by instrument name so other tests'
    /// emissions on the same meter don't pollute results.
    /// </summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener;
        public List<CapturedMeasurement> Measurements { get; } = new();

        public MeterCapture(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == RelationDiscoveryTelemetryRecorder.MeterName
                        && instrument.Name == instrumentName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((inst, value, tags, state) =>
                Measurements.Add(new CapturedMeasurement(value, value, ToDictionary(tags))));
            _listener.SetMeasurementEventCallback<double>((inst, value, tags, state) =>
                Measurements.Add(new CapturedMeasurement((long)value, value, ToDictionary(tags))));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>(tags.Length);
            foreach (var kv in tags)
            {
                dict[kv.Key] = kv.Value;
            }
            return dict;
        }
    }

    private sealed record CapturedMeasurement(long Value, double ValueAsDouble, IReadOnlyDictionary<string, object?> Tags);
}
