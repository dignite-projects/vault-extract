using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Extract.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Extract.Documents.Pipelines.Classification;

/// <summary>
/// Document classification workflow using MAF <c>ChatClientAgent</c> with structured output.
/// </summary>
public class DocumentClassificationWorkflow : ITransientDependency
{
    /// <summary>
    /// Structured-output (<c>RunAsync&lt;ClassificationResponse&gt;</c>), tool-free,
    /// prompt-unique call — routed through the dedicated keyed client
    /// (<see cref="ExtractConsts.StructuredChatClientKey"/>) so traces stay clean
    /// and hosts can optionally point classification at a smaller / cheaper model than
    /// the main agentic chat. See <c>docs/ai-provider.md</c> keyed-clients table.
    /// </summary>
    private readonly IChatClient _chatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly ExtractBehaviorOptions _options;

    public ILogger<DocumentClassificationWorkflow> Logger { get; set; }
        = NullLogger<DocumentClassificationWorkflow>.Instance;

    public DocumentClassificationWorkflow(
        [FromKeyedServices(ExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<ExtractBehaviorOptions> options,
        IPromptProvider promptProvider)
    {
        _chatClient = chatClient;
        _promptProvider = promptProvider;
        _options = options.Value;
    }

    public virtual async Task<DocumentClassificationOutcome> RunAsync(
        IReadOnlyList<DocumentType> candidateTypes,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (candidateTypes == null || candidateTypes.Count == 0)
        {
            return new DocumentClassificationOutcome
            {
                TypeCode = null,
                ConfidenceScore = 0,
                Reason = "No candidate types provided."
            };
        }

        // The caller (DocumentClassificationBackgroundJob) owns candidate ordering and limits.
        // Classification only needs the leading document semantics, so it intentionally feeds a
        // truncated prefix instead of the full text used by field extraction.
        var truncatedText = markdown;
        if (markdown.Length > _options.MaxTextLengthPerExtraction)
        {
            // Truncation drops the document tail; operations need visibility when key fields beyond
            // the cutoff may be missed.
            Logger.LogWarning(
                "Classification input truncated from {OriginalLength} to {TruncatedLength} characters; key fields beyond the cutoff will be missed.",
                markdown.Length, _options.MaxTextLengthPerExtraction);
            truncatedText = TruncateAtCharBoundary(markdown, _options.MaxTextLengthPerExtraction);
        }

        // Field architecture v2: DocumentType.DisplayName / Description are DB-resolved strings
        // entered by host or tenant admins in the admin UI, so they are user-controlled text.
        // They must be wrapped with PromptBoundary.WrapField (CLAUDE.md "Security Covenant /
        // PromptBoundary") to prevent malicious admin prompt injection via DisplayName / Description
        // (for example, "Ignore previous instructions..."). TypeCode is safe because the entity layer
        // already validates it as the <owner>.<sub-type> shape. Description (#262) is optional
        // classification helper text: append one line only when it is non-empty, without additional
        // content transformation.
        var typeDescriptions = candidateTypes.Select(t =>
        {
            var entry =
                $"- TypeCode: {t.TypeCode}\n" +
                $"  Name: {PromptBoundary.WrapField(t.DisplayName)}";
            if (!string.IsNullOrWhiteSpace(t.Description))
            {
                entry += $"\n  Description: {PromptBoundary.WrapField(t.Description)}";
            }
            return entry;
        }).ToList();

        var userMessage = $$"""
                ## Registered Document Types
                {{string.Join("\n", typeDescriptions)}}

                ## Document Markdown (first {{_options.MaxTextLengthPerExtraction}} characters)
                {{PromptBoundary.WrapDocument(truncatedText)}}
                """;

        var template = _promptProvider.GetClassificationPrompt(_options.DefaultLanguage);
        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "ExtractDocumentClassifier",
                ChatOptions = new ChatOptions
                {
                    Instructions = template.SystemInstructions + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<ClassificationResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        var parsed = response.Result;

        // The LLM occasionally returns percentage confidence values (for example 99.9) or truly
        // invalid values (NaN / <0 / >100). Percentages are normalized to 0..1 first; truly invalid
        // values are treated as "no trusted conclusion": typeCode is cleared and confidence is set
        // to 0, so the BackgroundJob takes the LowConfidence branch into manual review with the
        // UnresolvedClassification reason. This avoids Check.Range in
        // Document.ApplyAutomaticClassificationResult turning the whole PipelineRun into Failed.
        var rawConfidence = parsed?.Confidence ?? 0d;
        var typeCode = parsed?.TypeCode;
        if (!TryNormalizeConfidence(rawConfidence, out var confidenceScore))
        {
            Logger.LogWarning(
                "LLM returned out-of-range classification confidence {Confidence} (typeCode={TypeCode}); routing to manual review.",
                rawConfidence, typeCode);
            typeCode = null;
            confidenceScore = 0d;
        }
        else if (rawConfidence > 1d)
        {
            Logger.LogWarning(
                "LLM returned percentage classification confidence {Confidence} (typeCode={TypeCode}); normalized to {NormalizedConfidence}.",
                rawConfidence, typeCode, confidenceScore);
        }

        // Reason, the LLM's classification rationale, is passed through the outcome to the
        // BackgroundJob only for logging / diagnostics. Since #284 it is no longer persisted on
        // Document (the old ClassificationReason field was removed): high-confidence paths ignore
        // it, while low-confidence paths only log it. Operator-facing "why was this not classified"
        // context comes from DocumentPipelineRun ClassificationCandidates plus generic frontend copy.
        // Run.StatusMessage is not written on either path (MarkSucceeded accepts no statusMessage),
        // avoiding confusion with technical error messages.
        var outcome = new DocumentClassificationOutcome
        {
            TypeCode = typeCode,
            ConfidenceScore = confidenceScore,
            Reason = parsed?.Reason,
            // #346: container detection rides the same classification call (zero extra LLM cost). When set, it
            // dominates the type guess downstream — the BackgroundJob takes the container branch and ignores typeCode.
            IsContainer = parsed?.IsContainer ?? false,
            // #371: a non-container parent that embeds a standalone document (e.g. an invoice photo inside a
            // contract — a figure span the classification input shows bracketed by *[Image OCR]*…*[End OCR]*) still
            // triggers the unified sub-document detection pass. Rides the same call (zero extra LLM cost).
            ContainsEmbeddedDocument = parsed?.ContainsEmbeddedDocument ?? false
        };

        if (parsed?.Candidates != null)
        {
            foreach (var c in parsed.Candidates)
            {
                // Candidate confidence is only for UI display and run persistence. PipelineRunCandidate
                // is a plain record and does not Check.Range, so out-of-range values cannot damage the
                // aggregate root; clamp here so dirty values such as 1.5 do not leak to display code.
                outcome.Candidates.Add(new PipelineRunCandidate(c.TypeCode, ClampConfidence(c.Confidence)));
            }
        }

        return outcome;
    }

    // internal so Application.Tests can directly verify the regression-critical
    // out-of-range coercion logic (the surrounding 4-line branch in RunAsync is
    // trivially correct given correct helpers).
    internal static bool IsValidConfidence(double value)
        => !double.IsNaN(value) && value >= 0d && value <= 1d;

    internal static bool TryNormalizeConfidence(double value, out double normalized)
    {
        normalized = 0d;

        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
            return false;

        if (value <= 1d)
        {
            normalized = value;
            return true;
        }

        if (value <= 100d)
        {
            normalized = value / 100d;
            return true;
        }

        return false;
    }

    internal static double ClampConfidence(double value)
    {
        if (double.IsNaN(value))
            return 0d;
        if (value < 0d) return 0d;
        if (value > 1d) return 1d;
        return value;
    }

    // Truncate by UTF-16 code units without splitting surrogate pairs: when the last kept char is a
    // high surrogate whose low surrogate was cut off, drop it too. This avoids half a code point
    // degrading to U+FFFD when UTF-8 encoded for the LLM. The cut point is already in the discarded
    // document tail, so dropping one extra char is harmless. internal lets Application.Tests verify
    // the boundary logic directly, like the confidence helpers above.
    internal static string TruncateAtCharBoundary(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;

        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end];
    }

    private sealed class ClassificationResponse
    {
        public string? TypeCode { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }

        /// <summary>
        /// #346: <c>true</c> when the content is clearly several independent documents (a multi-type bundle or
        /// multiple instances of one type), so the document is a container that must not run field extraction on
        /// itself. Conservative by design (see the classification prompt); dominates <see cref="TypeCode"/> /
        /// <see cref="Confidence"/> when set.
        /// </summary>
        public bool IsContainer { get; set; }

        /// <summary>
        /// #371: <c>true</c> when the document is NOT a container but contains an embedded image that is itself a
        /// complete standalone document (an invoice photo inside a contract) — a figure span the classification input
        /// shows bracketed by <c>*[Image OCR]*…*[End OCR]*</c>. Triggers the unified sub-document detection pass for a
        /// concrete-typed parent. Conservative (the prompt's reject-list); ignored when <see cref="IsContainer"/> is set.
        /// </summary>
        public bool ContainsEmbeddedDocument { get; set; }

        public List<CandidateItem> Candidates { get; set; } = new();

        public sealed class CandidateItem
        {
            public string TypeCode { get; set; } = default!;
            public double Confidence { get; set; }
        }
    }
}

public class DocumentClassificationOutcome
{
    public string? TypeCode { get; set; }
    public double ConfidenceScore { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// #346: the document is a <b>container</b> (several independent documents bundled together). When <c>true</c>
    /// the BackgroundJob marks the document a container and suppresses field extraction; <see cref="TypeCode"/> /
    /// <see cref="ConfidenceScore"/> are ignored.
    /// </summary>
    public bool IsContainer { get; set; }

    /// <summary>
    /// #371: the document is a concrete-typed parent that <b>embeds</b> a standalone document (e.g. an invoice photo
    /// inside a contract). When <c>true</c> (and not a container) the BackgroundJob still publishes
    /// <c>DocumentClassifiedEto</c> (the parent extracts its own fields) <b>and</b> enqueues the unified sub-document
    /// detection pass to route the embedded document. Ignored when <see cref="IsContainer"/> is set.
    /// </summary>
    public bool ContainsEmbeddedDocument { get; set; }

    public List<PipelineRunCandidate> Candidates { get; } = new();
}
