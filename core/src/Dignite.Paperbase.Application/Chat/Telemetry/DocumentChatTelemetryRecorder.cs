using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Dignite.Paperbase.Abstractions.Chat;
using Microsoft.Extensions.Logging;
using Volo.Abp.Auditing;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Chat.Telemetry;

/// <summary>
/// Project-specific telemetry layer on top of the standard signals already emitted by
/// <c>Microsoft.Extensions.AI</c>'s <c>OpenTelemetryChatClient</c> /
/// <c>FunctionInvokingChatClient</c> when the host wires <c>.UseOpenTelemetry()</c>.
/// <para>
/// Standard signals (do NOT duplicate here):
/// <list type="bullet">
///   <item><c>gen_ai.client.operation.duration</c> — turn latency (s) histogram</item>
///   <item><c>gen_ai.client.token.usage</c> — input/output tokens histogram</item>
///   <item><c>gen_ai.client.operation.time_to_first_chunk</c> — streaming TTFB histogram</item>
///   <item>Activity span <c>"chat {model}"</c> — turn span with <c>gen_ai.*</c> tags</item>
///   <item>Activity span <c>"execute_tool {tool_name}"</c> — per-tool span</item>
/// </list>
/// </para>
/// <para>
/// What this recorder adds beyond the OTel standard:
/// <list type="bullet">
///   <item><c>paperbase.document_chat.turn.degraded</c> — counter for the project's
///     "honest signal" (CLAUDE.md): turns where the model declined to invoke search
///     OR retrieval threw and the turn fell back to context-only.</item>
///   <item><c>paperbase.document_chat.tool.result.size</c> — histogram of result
///     payload size (bytes), useful for spotting pathological tool outputs that
///     blow up LLM context.</item>
///   <item>Business-domain audit entries on <see cref="IAuditingManager"/>:
///     tenant/user/conversation/document/document-type — these are not in OTel scope
///     and link to the OTel trace via <c>Activity.Current?.TraceId</c>.</item>
/// </list>
/// </para>
/// </summary>
// Singleton lifetime matches the static Meter / instruments below — `System.Diagnostics.Metrics`
// recommends one shared Meter per process, and per-scope construction would only churn allocations.
public class DocumentChatTelemetryRecorder : ISingletonDependency
{
    public const string AuditToolCallsPropertyName = "DocumentChat.ToolCalls";
    public const string AuditTurnPropertyName = "DocumentChat.Turn";
    public const string MeterName = "Dignite.Paperbase.DocumentChat";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> DegradedTurns = Meter.CreateCounter<long>(
        "paperbase.document_chat.turn.degraded",
        description: "Number of chat turns that ran without retrieval grounding (model declined to invoke search OR retrieval failed).");
    private static readonly Histogram<long> ToolResultSize = Meter.CreateHistogram<long>(
        "paperbase.document_chat.tool.result.size", unit: "By",
        description: "Size of tool-call result payloads in bytes.");

    private readonly IAuditingManager _auditingManager;
    private readonly ILogger<DocumentChatTelemetryRecorder> _logger;

    public DocumentChatTelemetryRecorder(
        IAuditingManager auditingManager,
        ILogger<DocumentChatTelemetryRecorder> logger)
    {
        _auditingManager = auditingManager;
        _logger = logger;
    }

    public virtual void RecordToolCall(DocumentChatToolAuditEntry entry)
    {
        AddToolCallToAuditLog(entry);

        // Per-tool count + duration are emitted by Microsoft.Extensions.AI's
        // FunctionInvocationProcessor as `execute_tool {tool_name}` Activity spans
        // (see OTel GenAI semantic conventions). Don't duplicate here.
        if (entry.ResultSizeBytes.HasValue)
        {
            ToolResultSize.Record(entry.ResultSizeBytes.Value, CreateToolTags(entry));
        }

        if (entry.Outcome == DocumentChatTelemetryOutcome.Success)
        {
            _logger.LogInformation(
                "Document chat tool {ToolName} completed. ConversationId={ConversationId} TenantId={TenantId} DocumentTypeCode={DocumentTypeCode} ElapsedMs={ElapsedMs} ResultSizeBytes={ResultSizeBytes}",
                entry.ToolName,
                entry.ConversationId,
                entry.TenantId,
                entry.DocumentTypeCode,
                entry.ElapsedMs,
                entry.ResultSizeBytes);
        }
        else
        {
            _logger.LogWarning(
                "Document chat tool {ToolName} failed. ConversationId={ConversationId} TenantId={TenantId} DocumentTypeCode={DocumentTypeCode} ElapsedMs={ElapsedMs} ExceptionType={ExceptionType}",
                entry.ToolName,
                entry.ConversationId,
                entry.TenantId,
                entry.DocumentTypeCode,
                entry.ElapsedMs,
                entry.ExceptionType);
        }
    }

    public virtual void RecordTurn(DocumentChatTurnAuditEntry entry)
    {
        // Derive ToolCallSummary / ToolCallDepth / GroundingSource from the per-tool
        // entries already accumulated on the audit scope. Keeping the derivation here
        // (rather than asking the AppService to re-aggregate) guarantees the per-turn
        // counts always match the per-tool entries — they share one source of truth.
        var enriched = EnrichTurnEntryFromAuditScope(entry);

        AddTurnToAuditLog(enriched);

        // Turn count + duration + token usage are emitted by
        // Microsoft.Extensions.AI's OpenTelemetryChatClient as standard
        // gen_ai.* signals — don't duplicate here.
        // IsDegraded is the project's "honest signal" (CLAUDE.md) and has no
        // OTel-standard equivalent, so we count it explicitly.
        if (enriched.IsDegraded)
        {
            DegradedTurns.Add(1, CreateTurnTags(enriched));
        }

        if (enriched.Outcome == DocumentChatTelemetryOutcome.Success)
        {
            _logger.LogInformation(
                "Document chat turn completed. ConversationId={ConversationId} TenantId={TenantId} DocumentTypeCode={DocumentTypeCode} Streaming={Streaming} IsDegraded={IsDegraded} GroundingSource={GroundingSource} ToolCallDepth={ToolCallDepth} CitationCount={CitationCount} ElapsedMs={ElapsedMs}",
                enriched.ConversationId,
                enriched.TenantId,
                enriched.DocumentTypeCode,
                enriched.Streaming,
                enriched.IsDegraded,
                enriched.GroundingSource,
                enriched.ToolCallDepth,
                enriched.CitationCount,
                enriched.ElapsedMs);
        }
        else
        {
            _logger.LogWarning(
                "Document chat turn failed. ConversationId={ConversationId} TenantId={TenantId} DocumentTypeCode={DocumentTypeCode} Streaming={Streaming} GroundingSource={GroundingSource} ToolCallDepth={ToolCallDepth} ElapsedMs={ElapsedMs} ExceptionType={ExceptionType}",
                enriched.ConversationId,
                enriched.TenantId,
                enriched.DocumentTypeCode,
                enriched.Streaming,
                enriched.GroundingSource,
                enriched.ToolCallDepth,
                enriched.ElapsedMs,
                enriched.ExceptionType);
        }
    }

    /// <summary>
    /// Reads the per-tool entries already recorded on the current audit scope and
    /// returns a copy of <paramref name="entry"/> enriched with the derived per-turn
    /// dimensions (<see cref="DocumentChatTurnAuditEntry.ToolCallSummary"/>,
    /// <see cref="DocumentChatTurnAuditEntry.ToolCallDepth"/>,
    /// <see cref="DocumentChatTurnAuditEntry.GroundingSource"/>).
    /// </summary>
    /// <remarks>
    /// Counts include both successful and failed tool invocations — failures still
    /// reflect what the model attempted, which is what callers actually want to see in
    /// telemetry (e.g. "model retried 3 times before giving up").
    /// </remarks>
    protected virtual DocumentChatTurnAuditEntry EnrichTurnEntryFromAuditScope(DocumentChatTurnAuditEntry entry)
    {
        var toolCalls = ReadToolCallsFromAuditScope();

        var summary = toolCalls.Count == 0
            ? null
            : (IReadOnlyDictionary<string, int>)toolCalls
                .GroupBy(t => t.ToolName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new DocumentChatTurnAuditEntry
        {
            ConversationId = entry.ConversationId,
            UserId = entry.UserId,
            TenantId = entry.TenantId,
            DocumentId = entry.DocumentId,
            DocumentTypeCode = entry.DocumentTypeCode,
            TraceId = entry.TraceId,
            Streaming = entry.Streaming,
            CitationCount = entry.CitationCount,
            IsDegraded = entry.IsDegraded,
            ElapsedMs = entry.ElapsedMs,
            Outcome = entry.Outcome,
            ExceptionType = entry.ExceptionType,
            ToolCallSummary = summary,
            ToolCallDepth = toolCalls.Count,
            GroundingSource = ClassifyGrounding(toolCalls),
            CitationsTrimmed = entry.CitationsTrimmed,
            AnchorResolutionFailed = entry.AnchorResolutionFailed
        };
    }

    /// <summary>
    /// Returns the <see cref="GroundingSource"/> for the in-flight turn, derived from
    /// the per-tool entries already on the current audit scope. Used by
    /// <c>DocumentChatAppService</c> to derive the user-facing
    /// <see cref="ChatTurnResultDto.IsDegraded"/> signal from the same source of truth
    /// as the audit entry — guaranteeing the two never disagree.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="GroundingSource.None"/> when no audit scope is active or no
    /// tool has been invoked yet; the caller will treat that as "answer not grounded".
    /// </remarks>
    public virtual GroundingSource GetCurrentTurnGroundingSource()
        => ClassifyGrounding(ReadToolCallsFromAuditScope());

    /// <summary>
    /// Classifies a turn's grounding source from the tool names invoked.
    /// Override to extend classification (e.g. when a future business module
    /// contributes another vector-style search tool).
    /// </summary>
    protected virtual GroundingSource ClassifyGrounding(IReadOnlyList<DocumentChatToolAuditEntry> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return GroundingSource.None;
        }

        var hasVector = false;
        var hasStructured = false;
        foreach (var t in toolCalls)
        {
            if (IsVectorSearchTool(t.ToolName))
            {
                hasVector = true;
            }
            else
            {
                hasStructured = true;
            }

            if (hasVector && hasStructured)
            {
                break;
            }
        }

        return (hasVector, hasStructured) switch
        {
            (true, true) => GroundingSource.Mixed,
            (true, false) => GroundingSource.Vector,
            (false, true) => GroundingSource.Structured,
            _ => GroundingSource.None
        };
    }

    /// <summary>
    /// Returns true if <paramref name="toolName"/> identifies a vector-search tool
    /// (currently just the built-in <see cref="ChatConsts.SearchPaperbaseDocumentsToolName"/>).
    /// </summary>
    protected virtual bool IsVectorSearchTool(string toolName)
        => string.Equals(toolName, ChatConsts.SearchPaperbaseDocumentsToolName, StringComparison.Ordinal);

    private IReadOnlyList<DocumentChatToolAuditEntry> ReadToolCallsFromAuditScope()
    {
        var scope = _auditingManager.Current;
        if (scope?.Log == null)
        {
            return Array.Empty<DocumentChatToolAuditEntry>();
        }

        if (scope.Log.ExtraProperties.TryGetValue(AuditToolCallsPropertyName, out var existing)
            && existing is List<DocumentChatToolAuditEntry> entries)
        {
            return entries;
        }

        return Array.Empty<DocumentChatToolAuditEntry>();
    }

    private void AddToolCallToAuditLog(DocumentChatToolAuditEntry entry)
    {
        var scope = _auditingManager.Current;
        if (scope?.Log == null)
        {
            return;
        }

        if (!scope.Log.ExtraProperties.TryGetValue(AuditToolCallsPropertyName, out var existing)
            || existing is not List<DocumentChatToolAuditEntry> entries)
        {
            entries = new List<DocumentChatToolAuditEntry>();
            scope.Log.ExtraProperties[AuditToolCallsPropertyName] = entries;
        }

        entries.Add(entry);
    }

    private void AddTurnToAuditLog(DocumentChatTurnAuditEntry entry)
    {
        var scope = _auditingManager.Current;
        if (scope?.Log == null)
        {
            return;
        }

        scope.Log.ExtraProperties[AuditTurnPropertyName] = entry;
    }

    private static KeyValuePair<string, object?>[] CreateToolTags(DocumentChatToolAuditEntry entry)
        => new[]
        {
            new KeyValuePair<string, object?>("tool.name", entry.ToolName),
            new KeyValuePair<string, object?>("outcome", entry.Outcome.ToString()),
            new KeyValuePair<string, object?>("document.type", entry.DocumentTypeCode ?? "(none)")
        };

    private static KeyValuePair<string, object?>[] CreateTurnTags(DocumentChatTurnAuditEntry entry)
        => new[]
        {
            new KeyValuePair<string, object?>("outcome", entry.Outcome.ToString()),
            new KeyValuePair<string, object?>("document.type", entry.DocumentTypeCode ?? "(none)"),
            new KeyValuePair<string, object?>("streaming", entry.Streaming),
            new KeyValuePair<string, object?>("grounding_source", entry.GroundingSource.ToString())
        };
}

public enum DocumentChatTelemetryOutcome
{
    Success = 0,
    Failure = 1
}

public sealed class DocumentChatToolAuditEntry
{
    public required Guid ConversationId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? TenantId { get; init; }
    public Guid? DocumentId { get; init; }
    public string? DocumentTypeCode { get; init; }
    public string? TraceId { get; init; }
    public required string ToolName { get; init; }
    public required IReadOnlyDictionary<string, object?> ArgumentsSummary { get; init; }
    public IReadOnlyDictionary<string, object?>? ResultSummary { get; init; }
    public long? ResultSizeBytes { get; init; }
    public required double ElapsedMs { get; init; }
    public required DocumentChatTelemetryOutcome Outcome { get; init; }
    public string? ExceptionType { get; init; }
}

// Token usage (input/output/cached/reasoning) is a Microsoft.Extensions.AI signal:
// when the host wires `OpenTelemetryChatClient` (see PaperbaseHostModule.ConfigureAI),
// the gen_ai.client.token.usage histogram emits each turn's token counts. Audit
// rows correlate with that telemetry through TraceId; carrying the raw counts on
// the audit entry as well would only re-emit the same data.
public sealed class DocumentChatTurnAuditEntry
{
    public required Guid ConversationId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? TenantId { get; init; }
    public Guid? DocumentId { get; init; }
    public string? DocumentTypeCode { get; init; }
    public string? TraceId { get; init; }
    public required bool Streaming { get; init; }
    public int CitationCount { get; init; }
    public bool IsDegraded { get; init; }
    public required double ElapsedMs { get; init; }
    public required DocumentChatTelemetryOutcome Outcome { get; init; }
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Per-tool invocation count for this turn (tool name → count). <c>null</c> when no
    /// tool was invoked. Derived from the audit scope's per-tool entries by
    /// <see cref="DocumentChatTelemetryRecorder.RecordTurn"/> — callers do not need to
    /// supply this; it is overwritten if they do.
    /// </summary>
    public IReadOnlyDictionary<string, int>? ToolCallSummary { get; init; }

    /// <summary>
    /// Total number of tool invocations in this turn (sum of <see cref="ToolCallSummary"/>
    /// values). Includes failed invocations because they reflect the model's actual
    /// behavior. Derived by the recorder.
    /// </summary>
    public int ToolCallDepth { get; init; }

    /// <summary>
    /// Categorizes which kinds of tools the model invoked in this turn. Derived by the
    /// recorder. See <see cref="GroundingSource"/> for the classification rule.
    /// </summary>
    public GroundingSource GroundingSource { get; init; }

    /// <summary>
    /// True when the per-turn citation cap (see <c>PaperbaseAIBehaviorOptions.MaxCapturedCitations</c>)
    /// was hit and additional vector-search hits were dropped. Surfaces pathological LLM
    /// retry loops or queries that fan out into very large recall sets — both worth alerting on.
    /// </summary>
    public bool CitationsTrimmed { get; init; }

    /// <summary>
    /// True when the conversation has an anchor <see cref="DocumentId"/> but the per-turn
    /// anchor lookup degraded (document deleted, tenant mismatch, or caller lost the
    /// <c>Documents.Default</c> permission). Issue #100 reverse example E mandates that
    /// the turn proceeds **without** the anchor hint rather than throwing — this signal
    /// is how operators see that "permission drift" is happening at scale.
    /// </summary>
    public bool AnchorResolutionFailed { get; init; }
}
