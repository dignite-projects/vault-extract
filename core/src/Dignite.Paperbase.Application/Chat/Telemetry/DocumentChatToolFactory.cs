using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Chat;
using Microsoft.Extensions.AI;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Chat.Telemetry;

public class DocumentChatToolFactory : IDocumentChatToolFactory, ITransientDependency
{
    // Short-prefix hash is plenty for dedup/correlation in audit/metrics; longer
    // prefixes give attackers more rainbow-table grip on free-form natural-language
    // arguments such as the LLM-supplied `query` parameter to search_paperbase_documents.
    private const int HashHexPrefixLength = 12;
    private const int MaxCollectionItems = 5;

    private static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "secret",
        "token",
        "apikey",
        "api_key",
        "authorization"
    ];

    private readonly DocumentChatTelemetryRecorder _recorder;

    public DocumentChatToolFactory(DocumentChatTelemetryRecorder recorder)
    {
        _recorder = recorder;
    }

    // Issue #130: split into two overloads (mirroring the interface) so external
    // implementers compiled against the pre-#129 interface still bind. The 4-arg
    // is the original required member; the 5-arg is the opt-in describer-aware
    // overload introduced for #116.
    public virtual AIFunction Create(
        DocumentChatToolContext ctx,
        Delegate method,
        string name,
        string description)
        => Create(ctx, method, name, description, progressDescriber: null);

    public virtual AIFunction Create(
        DocumentChatToolContext ctx,
        Delegate method,
        string name,
        string description,
        Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber)
    {
        var inner = AIFunctionFactory.Create(method, name, description);
        return new AuditedDocumentChatFunction(inner, ctx, _recorder, progressDescriber);
    }

    private static IReadOnlyDictionary<string, object?> SummarizeArguments(AIFunctionArguments? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments == null)
        {
            return result;
        }

        foreach (var (key, value) in arguments)
        {
            result[key] = IsSensitiveKey(key)
                ? "***"
                : SummarizeValue(value);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> SummarizeResult(object? result)
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = result?.GetType().Name ?? "null"
        };

        switch (result)
        {
            case null:
                summary["sizeBytes"] = 0;
                break;
            case JsonElement json:
                SummarizeJsonElement(json, summary);
                break;
            case string text:
                SummarizeString(text, summary);
                break;
            default:
                var textValue = Convert.ToString(result);
                if (!string.IsNullOrEmpty(textValue))
                {
                    SummarizeString(textValue, summary);
                }
                break;
        }

        return summary;
    }

    private static long? GetResultSizeBytes(IReadOnlyDictionary<string, object?> resultSummary)
        => resultSummary.TryGetValue("sizeBytes", out var value) && value is long size
            ? size
            : null;

    private static void SummarizeJsonElement(JsonElement json, Dictionary<string, object?> summary)
    {
        summary["kind"] = "json";
        summary["sizeBytes"] = Encoding.UTF8.GetByteCount(json.GetRawText());

        if (json.ValueKind == JsonValueKind.String)
        {
            var value = json.GetString() ?? string.Empty;
            summary["stringLength"] = value.Length;
            summary["documentTagCount"] = CountDocumentTags(value);
            return;
        }

        if (json.ValueKind == JsonValueKind.Array)
        {
            summary["itemCount"] = json.GetArrayLength();
            return;
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "documentIds", "contracts", "buckets" })
            {
                if (json.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Array)
                {
                    summary[$"{propertyName}Count"] = property.GetArrayLength();
                }
            }

            if (json.TryGetProperty("found", out var found)
                && found.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                summary["found"] = found.GetBoolean();
            }
        }
    }

    private static void SummarizeString(string text, Dictionary<string, object?> summary)
    {
        summary["kind"] = "string";
        summary["sizeBytes"] = Encoding.UTF8.GetByteCount(text);
        summary["stringLength"] = text.Length;
        summary["documentTagCount"] = CountDocumentTags(text);

        try
        {
            using var doc = JsonDocument.Parse(text);
            SummarizeJsonElement(doc.RootElement, summary);
        }
        catch (JsonException)
        {
            // Plain text tool results are expected for the RAG context block.
        }
    }

    private static int CountDocumentTags(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("<document ", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "<document ".Length;
        }

        return count;
    }

    private static object? SummarizeValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement json:
                return SummarizeJsonValue(json);
            case string text:
                return HashStringForAudit(text);
            case Guid or DateTime or DateOnly or TimeOnly or bool:
                return value;
            case int or long or short or byte or decimal or double or float:
                return value;
            case IEnumerable enumerable when value is not string:
                return SummarizeEnumerable(enumerable);
            default:
                // Unknown type → ToString may surface PII; only structural metadata is recorded.
                return HashStringForAudit(Convert.ToString(value) ?? string.Empty);
        }
    }

    private static object? SummarizeJsonValue(JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.String => HashStringForAudit(json.GetString() ?? string.Empty),
            JsonValueKind.Number => json.TryGetInt64(out var l) ? l : json.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => new
            {
                count = json.GetArrayLength(),
                sample = json.EnumerateArray().Take(MaxCollectionItems).Select(SummarizeJsonValue).ToList()
            },
            JsonValueKind.Object => new
            {
                properties = json.EnumerateObject().Select(p => p.Name).Take(MaxCollectionItems).ToList()
            },
            _ => json.ValueKind.ToString()
        };
    }

    private static object SummarizeEnumerable(IEnumerable enumerable)
    {
        var sample = new List<object?>();
        var count = 0;

        foreach (var item in enumerable)
        {
            if (count < MaxCollectionItems)
            {
                sample.Add(SummarizeValue(item));
            }

            count++;
        }

        return new { count, sample };
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reduces a free-form string argument or result fragment to structural metadata
    /// only — never the raw text. The LLM-supplied <c>query</c> argument to
    /// <c>search_paperbase_documents</c> and similar contributor-tool inputs (party
    /// names, contract numbers, free-form questions) frequently contain PII that
    /// would otherwise be persisted indefinitely in <c>AbpAuditLogs.Comments</c>.
    /// </summary>
    /// <remarks>
    /// Hash prefix is short (12 hex chars = 48 bits) — enough for dedup/correlation
    /// across audit/metrics, not enough to surface plaintext.
    /// </remarks>
    private static object HashStringForAudit(string value)
    {
        return new
        {
            kind = "string",
            length = value.Length,
            hash = ComputeHashPrefix(value)
        };
    }

    private static string ComputeHashPrefix(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, HashHexPrefixLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// Internal so <c>DocumentChatAppService</c> (same assembly) can downcast on the
    /// streaming path to fetch <see cref="ProgressDescriber"/> for
    /// <c>ToolCallStarted</c> events. External consumers should treat the returned
    /// <see cref="AIFunction"/> as opaque.
    /// </summary>
    internal sealed class AuditedDocumentChatFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly DocumentChatToolContext _ctx;
        private readonly DocumentChatTelemetryRecorder _recorder;

        /// <summary>
        /// Issue #116: optional sanitized-progress describer supplied at registration
        /// time. <c>null</c> when the tool didn't opt in; the streaming AppService
        /// falls back to a generic "正在执行 {ToolName}" label.
        /// </summary>
        public Func<IReadOnlyDictionary<string, object?>, string?>? ProgressDescriber { get; }

        public AuditedDocumentChatFunction(
            AIFunction inner,
            DocumentChatToolContext ctx,
            DocumentChatTelemetryRecorder recorder,
            Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber = null)
        {
            _inner = inner;
            _ctx = ctx;
            _recorder = recorder;
            ProgressDescriber = progressDescriber;
        }

        public override string Name => _inner.Name;
        public override string Description => _inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;
        public override JsonElement JsonSchema => _inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.InvokeAsync(arguments, cancellationToken);
                sw.Stop();

                var resultSummary = SummarizeResult(result);
                _recorder.RecordToolCall(new DocumentChatToolAuditEntry
                {
                    ConversationId = _ctx.ConversationId,
                    UserId = _ctx.UserId,
                    TenantId = _ctx.TenantId,
                    DocumentId = _ctx.DocumentId,
                    DocumentTypeCode = _ctx.DocumentTypeCode,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    ToolName = Name,
                    ArgumentsSummary = SummarizeArguments(arguments),
                    ResultSummary = resultSummary,
                    ResultSizeBytes = GetResultSizeBytes(resultSummary),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Outcome = DocumentChatTelemetryOutcome.Success
                });

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _recorder.RecordToolCall(new DocumentChatToolAuditEntry
                {
                    ConversationId = _ctx.ConversationId,
                    UserId = _ctx.UserId,
                    TenantId = _ctx.TenantId,
                    DocumentId = _ctx.DocumentId,
                    DocumentTypeCode = _ctx.DocumentTypeCode,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    ToolName = Name,
                    ArgumentsSummary = SummarizeArguments(arguments),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Outcome = DocumentChatTelemetryOutcome.Failure,
                    ExceptionType = ex.GetType().FullName
                });
                throw;
            }
        }
    }
}
