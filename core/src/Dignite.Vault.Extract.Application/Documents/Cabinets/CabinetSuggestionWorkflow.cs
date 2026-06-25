using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Workflow for "blank cabinet AI fallback selection" (#265): choose the best-matching cabinet for a
/// document from current-layer candidates, or abstain when there is no clear match. Orthogonal to the
/// content pipeline: it creates no PipelineRun and does not participate in the Ready gate (#194).
/// Security rules are documented in llm-call-anti-patterns.md.
/// </summary>
public class CabinetSuggestionWorkflow : ITransientDependency
{
    /// <summary>Uses the same <see cref="ExtractConsts.StructuredChatClientKey"/> keyed client as classification (structured-output, tool-free), so hosts can point it at a smaller / cheaper model.</summary>
    private readonly IChatClient _chatClient;
    private readonly ExtractBehaviorOptions _options;

    public ILogger<CabinetSuggestionWorkflow> Logger { get; set; }
        = NullLogger<CabinetSuggestionWorkflow>.Instance;

    public CabinetSuggestionWorkflow(
        [FromKeyedServices(ExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<ExtractBehaviorOptions> options)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    /// <summary>Compile-time constant system instructions for injection resistance; concatenate no runtime strings. The LLM chooses one number or abstains, preferring omission over a poor match.</summary>
    private const string SystemPrompt =
        "You help organize an uploaded document into the best-matching filing cabinet. " +
        "Cabinets are a human organizational dimension (e.g. by department, project, or batch) and are " +
        "independent of the document's content type. You are given a numbered list of the available " +
        "cabinets (each with a name and an optional description) and the beginning of the document (as Markdown). " +
        "Pick the single cabinet whose name and description best fit the document, and report your confidence (0.0 to 1.0). " +
        "If no cabinet clearly fits, return null for cabinetIndex with a low confidence — it is better to " +
        "leave the document uncategorized than to file it into a poorly matching cabinet. " +
        "Return JSON only: {\"cabinetIndex\": <1-based number or null>, \"confidence\": <0.0-1.0>}.";

    /// <summary>
    /// Chooses a cabinet for <paramref name="markdown"/> from the candidates. A <c>null</c>
    /// <see cref="CabinetSuggestionOutcome.CabinetId"/> means abstention: no candidates, LLM
    /// abstained, or the returned index was out of range. The caller owns confidence threshold
    /// decisions; this method only parses and maps.
    /// </summary>
    public virtual async Task<CabinetSuggestionOutcome> RunAsync(
        IReadOnlyList<Cabinet> candidates,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return CabinetSuggestionOutcome.None;
        }

        // Cabinet selection only needs the leading document semantics, so truncate to the prefix using
        // the same strategy as classification.
        var truncatedText = markdown;
        if (markdown.Length > _options.MaxTextLengthPerExtraction)
        {
            Logger.LogWarning(
                "Cabinet suggestion input truncated from {OriginalLength} to {TruncatedLength} characters.",
                markdown.Length, _options.MaxTextLengthPerExtraction);
            truncatedText = TruncateAtCharBoundary(markdown, _options.MaxTextLengthPerExtraction);
        }

        var numbered = FormatCandidates(candidates);

        var userMessage = $$"""
                ## Available Cabinets
                {{numbered}}

                ## Document Markdown (first {{_options.MaxTextLengthPerExtraction}} characters)
                {{PromptBoundary.WrapDocument(truncatedText)}}
                """;

        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "ExtractCabinetSuggester",
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemPrompt + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<CabinetSuggestionResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        return ResolveOutcome(response.Result, candidates);
    }

    /// <summary>
    /// Formats candidate cabinets as a 1-based numbered list for the LLM. Use numbers instead of
    /// echoed Guid / Name values to avoid LLM GUID copy errors and naturally resist injection because
    /// it can only choose from preloaded candidates. Name / Description are user-controlled text and
    /// must be wrapped with <c>PromptBoundary.WrapField</c>. Empty Description emits only Name,
    /// mirroring <c>DocumentClassificationWorkflow</c>'s optional description concatenation. internal
    /// lets Application.Tests verify the concatenation format directly.
    /// </summary>
    internal static string FormatCandidates(IReadOnlyList<Cabinet> candidates)
    {
        var lines = candidates.Select((c, i) =>
        {
            var line = $"{i + 1}. {PromptBoundary.WrapField(c.Name)}";
            if (!string.IsNullOrWhiteSpace(c.Description))
            {
                line += $" — {PromptBoundary.WrapField(c.Description)}";
            }
            return line;
        });
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Maps the LLM <see cref="CabinetSuggestionResponse"/> to <see cref="CabinetSuggestionOutcome"/>:
    /// a 1-based index maps back to candidate <see cref="Cabinet.Id"/>; out-of-range / null abstains;
    /// confidence is clamped to 0..1. internal lets tests verify boundary behavior.
    /// </summary>
    internal CabinetSuggestionOutcome ResolveOutcome(
        CabinetSuggestionResponse? parsed,
        IReadOnlyList<Cabinet> candidates)
    {
        if (parsed?.CabinetIndex is not { } index)
        {
            return CabinetSuggestionOutcome.None;
        }

        // 1-based index. Out-of-range values, including 0, negatives, or values beyond the candidate
        // count, abstain instead of writing a dirty cabinet.
        if (index < 1 || index > candidates.Count)
        {
            Logger.LogWarning(
                "Cabinet suggestion returned out-of-range index {Index} for {CandidateCount} candidates; abstaining.",
                index, candidates.Count);
            return CabinetSuggestionOutcome.None;
        }

        return new CabinetSuggestionOutcome
        {
            CabinetId = candidates[index - 1].Id,
            Confidence = ClampConfidence(parsed.Confidence)
        };
    }

    internal static double ClampConfidence(double value)
    {
        if (double.IsNaN(value))
            return 0d;
        if (value < 0d) return 0d;
        if (value > 1d) return 1d;
        return value;
    }

    // Truncate by UTF-16 code units without splitting surrogate pairs, matching
    // DocumentClassificationWorkflow.TruncateAtCharBoundary. internal lets Application.Tests verify
    // the boundary logic directly.
    internal static string TruncateAtCharBoundary(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;

        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end];
    }

    internal sealed class CabinetSuggestionResponse
    {
        /// <summary>1-based candidate index; <c>null</c> means the LLM abstained because there was no clear match.</summary>
        public int? CabinetIndex { get; set; }

        public double Confidence { get; set; }
    }
}

/// <summary>
/// Cabinet selection result. A <c>null</c> <see cref="CabinetId"/> means abstention (no candidates,
/// LLM abstained, or index out of range), so the caller keeps the document uncategorized.
/// </summary>
public sealed class CabinetSuggestionOutcome
{
    public Guid? CabinetId { get; init; }

    public double Confidence { get; init; }

    public static CabinetSuggestionOutcome None { get; } = new() { CabinetId = null, Confidence = 0d };
}
