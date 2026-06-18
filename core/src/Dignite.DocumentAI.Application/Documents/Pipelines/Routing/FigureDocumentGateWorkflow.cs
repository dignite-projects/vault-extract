using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents.DocumentTypes;
using Dignite.DocumentAI.Documents.Pipelines.Classification;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.DocumentAI.Documents.Pipelines.Routing;

/// <summary>
/// Scenario B figure-document gate (#365). Answers the binary question that actually gates figure routing —
/// "is this embedded figure a self-contained, standalone document, or merely an element of its parent?" —
/// instead of reusing the whole-document <see cref="DocumentClassificationWorkflow"/> as a document detector
/// (which is primed to <i>assign a type</i> and assumes its input already is a document). The judgment is:
/// <list type="bullet">
///   <item><b>Conservative</b> — modeled on the classification <c>isContainer</c> reject-list, so incidental
///   text on a chart / logo / stamp / photo / diagram does not promote the figure to a document.</item>
///   <item><b>Parent-aware</b> — the parent document's title and type are fed in so independence is judged
///   against the parent, not in a vacuum.</item>
/// </list>
/// Tool-free, structured-output, prompt-unique call routed through the dedicated <see cref="DocumentAIConsts.StructuredChatClientKey"/>
/// keyed client, exactly like <see cref="DocumentClassificationWorkflow"/>. Spawning is gated by
/// <see cref="DocumentAIBehaviorOptions.FigureGateConfidenceThreshold"/>, a dedicated bar — the matched type's
/// Ready-gate <c>ConfidenceThreshold</c> is intentionally not consulted (the spawned document re-classifies
/// itself on its own pipeline run).
/// </summary>
public class FigureDocumentGateWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly DocumentAIBehaviorOptions _options;

    public ILogger<FigureDocumentGateWorkflow> Logger { get; set; }
        = NullLogger<FigureDocumentGateWorkflow>.Instance;

    public FigureDocumentGateWorkflow(
        [FromKeyedServices(DocumentAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<DocumentAIBehaviorOptions> options,
        IPromptProvider promptProvider)
    {
        _chatClient = chatClient;
        _promptProvider = promptProvider;
        _options = options.Value;
    }

    public virtual async Task<FigureGateOutcome> RunAsync(
        IReadOnlyList<DocumentType> candidateTypes,
        string transcription,
        FigureGateParentContext parent,
        CancellationToken cancellationToken = default)
    {
        // Candidate types are grounding only ("what counts as a document in this tenant"); the gate does not
        // assign one. DisplayName / Description are admin-entered user-controlled text and must be
        // PromptBoundary-wrapped (same discipline as DocumentClassificationWorkflow); TypeCode is validated
        // <owner>.<sub-type> shape, so it is safe raw.
        var typeDescriptions = (candidateTypes ?? new List<DocumentType>()).Select(t =>
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

        // A figure transcription is small in practice, but cap defensively against a pathological crop so it
        // cannot blow the prompt. Reuses the classification truncation bound and surrogate-safe cut.
        var truncated = transcription ?? string.Empty;
        if (truncated.Length > _options.MaxTextLengthPerExtraction)
        {
            Logger.LogDebug(
                "Figure gate transcription truncated from {OriginalLength} to {MaxLength} characters.",
                truncated.Length, _options.MaxTextLengthPerExtraction);
            truncated = DocumentClassificationWorkflow.TruncateAtCharBoundary(truncated, _options.MaxTextLengthPerExtraction);
        }

        var userMessage = $$"""
                ## Parent Document
                {{BuildParentBlock(parent)}}

                ## Registered Document Types (for grounding only)
                {{string.Join("\n", typeDescriptions)}}

                ## Figure Transcription
                {{PromptBoundary.WrapDocument(truncated)}}
                """;

        var template = _promptProvider.GetFigureGatePrompt(_options.DefaultLanguage);
        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "DocumentAIFigureDocumentGate",
                ChatOptions = new ChatOptions
                {
                    Instructions = template.SystemInstructions + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<FigureGateResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        var parsed = response.Result;
        var isStandalone = parsed?.IsStandaloneDocument ?? false;
        var rawConfidence = parsed?.Confidence ?? 0d;

        // Reuse the classification confidence coercion (percentages -> 0..1, truly invalid -> no conclusion). A
        // figure gate that cannot produce a trusted confidence must fail closed: do not spawn.
        if (!DocumentClassificationWorkflow.TryNormalizeConfidence(rawConfidence, out var confidence))
        {
            Logger.LogWarning(
                "Figure gate returned out-of-range confidence {Confidence} (isStandaloneDocument={IsStandalone}); treating as not a document.",
                rawConfidence, isStandalone);
            isStandalone = false;
            confidence = 0d;
        }

        return new FigureGateOutcome
        {
            IsStandaloneDocument = isStandalone,
            ConfidenceScore = confidence,
            Reason = parsed?.Reason
        };
    }

    /// <summary>
    /// Renders the parent-context block. Title is user-derived (a snapshot of the parent's Markdown) and the
    /// type DisplayName is admin-entered, so both are PromptBoundary-wrapped; the parent TypeCode is validated
    /// shape and safe raw. The parent type may be absent: routing and classification are enqueued in parallel,
    /// so the parent is often not yet classified when its figures route — this context is best-effort grounding.
    /// </summary>
    private static string BuildParentBlock(FigureGateParentContext parent)
    {
        var lines = new List<string>();
        if (parent != null && !string.IsNullOrWhiteSpace(parent.Title))
        {
            lines.Add($"- Title: {PromptBoundary.WrapField(parent.Title)}");
        }
        if (parent != null && !string.IsNullOrWhiteSpace(parent.DocumentTypeCode))
        {
            lines.Add($"- TypeCode: {parent.DocumentTypeCode}");
            if (!string.IsNullOrWhiteSpace(parent.DocumentTypeDisplayName))
            {
                lines.Add($"  TypeName: {PromptBoundary.WrapField(parent.DocumentTypeDisplayName)}");
            }
        }

        // System note (compile-time constant) -> raw, not wrapped: tells the model the parent is unclassified
        // rather than leaving the section blank.
        return lines.Count > 0 ? string.Join("\n", lines) : "- (parent metadata not yet available)";
    }

    private sealed class FigureGateResponse
    {
        public bool IsStandaloneDocument { get; set; }
        public double Confidence { get; set; }
        public string? Reason { get; set; }
    }
}

/// <summary>
/// Outcome of the figure-document gate (#365). <see cref="IsStandaloneDocument"/> is the load-bearing binary;
/// <see cref="ConfidenceScore"/> is the gate's confidence in that decision, compared against
/// <see cref="DocumentAIBehaviorOptions.FigureGateConfidenceThreshold"/>. <see cref="Reason"/> is diagnostics only.
/// </summary>
public class FigureGateOutcome
{
    public bool IsStandaloneDocument { get; set; }
    public double ConfidenceScore { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Parent-document context fed to the figure gate so it judges the figure's independence FROM the parent rather
/// than classifying a fragment in a vacuum (#365). All fields are nullable / best-effort: <see cref="Title"/> is
/// reliably set by the text-extraction stage, but the parent type is often still null because routing and
/// classification run in parallel.
/// </summary>
public sealed record FigureGateParentContext(
    string? Title,
    string? DocumentTypeCode,
    string? DocumentTypeDisplayName);
