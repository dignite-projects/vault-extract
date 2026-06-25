using System.Collections.Generic;
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

namespace Dignite.Vault.Extract.Documents.Pipelines.Segmentation;

/// <summary>
/// Unified sub-document detection pass (#371): one LLM pass over a source document's <c>Document.Markdown</c> (which
/// retains the inline <c>*[Image OCR]*…*[End OCR]*</c> figure provenance markers, #381) that decides, per span —
/// text spans and figure spans alike — whether it is a standalone sub-document or content of the parent. It folds
/// the born-digital container segmentation (#346) and the figure-document gate (#306/#365) into a single decision
/// made with full surrounding context.
/// <para>
/// Per the locked design (#346 Decision Log) the LLM returns only <b>verbatim start markers</b> + an
/// is-sub-document flag; <see cref="MarkdownSlicer"/> does the deterministic cutting, so the LLM never regenerates
/// content and the split is verifiable. Mirrors <see cref="Classification.DocumentClassificationWorkflow"/>:
/// tool-free, structured output, routed through the dedicated keyed
/// <see cref="ExtractConsts.StructuredChatClientKey"/> client; no <c>AIContextProviders</c> (channel layer, not
/// RAG); the Markdown is wrapped with <see cref="PromptBoundary.WrapDocument"/>, the parent title / type are
/// wrapped with <see cref="PromptBoundary.WrapField"/>, and the boundary rule is appended to the instructions.
/// </para>
/// </summary>
public class DocumentSegmentationWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly ExtractBehaviorOptions _options;

    public ILogger<DocumentSegmentationWorkflow> Logger { get; set; }
        = NullLogger<DocumentSegmentationWorkflow>.Instance;

    public DocumentSegmentationWorkflow(
        [FromKeyedServices(ExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<ExtractBehaviorOptions> options,
        IPromptProvider promptProvider)
    {
        _chatClient = chatClient;
        _promptProvider = promptProvider;
        _options = options.Value;
    }

    public virtual async Task<DocumentSegmentationOutcome> RunAsync(
        string markdown,
        SubDocumentDetectionContext context,
        CancellationToken cancellationToken = default)
    {
        var outcome = new DocumentSegmentationOutcome();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return outcome;
        }

        // The WHOLE Markdown is fed: sub-document boundaries can be anywhere, and a truncated tail would
        // silently lose the last documents. Output stays small regardless of input size because only short verbatim
        // markers are returned.
        var userMessage = $$"""
                ## Parent Document
                {{BuildParentBlock(context)}}

                ## Document Markdown
                {{PromptBoundary.WrapDocument(markdown)}}
                """;

        var template = _promptProvider.GetSegmentationPrompt(_options.DefaultLanguage);
        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "ExtractSubDocumentDetector",
                ChatOptions = new ChatOptions
                {
                    Instructions = template.SystemInstructions + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<SegmentationResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        var parsed = response.Result;
        var droppedBlankMarkers = 0;
        if (parsed?.Segments != null)
        {
            foreach (var segment in parsed.Segments)
            {
                // A blank marker cannot be located in the Markdown, so it would only poison the slice; drop it
                // here and let MarkdownSlicer's verification decide whether the remaining set is trustworthy.
                if (!string.IsNullOrWhiteSpace(segment.StartMarker))
                {
                    outcome.Boundaries.Add(new SegmentBoundary(segment.StartMarker, segment.IsSubDocument));
                }
                else
                {
                    droppedBlankMarkers++;
                }
            }
        }

        // Drift visibility (same discipline as the other structured paths): surface what the LLM returned so a
        // systematic regression — empty output, or all-blank / unmatchable markers — is diagnosable at the call
        // site, not only as a downstream SegmentationIncomplete review flag. MarkdownSlicer then verifies each
        // marker verbatim against the Markdown.
        Logger.LogInformation(
            "Sub-document detection proposed {BoundaryCount} boundaries ({DroppedBlankMarkers} blank markers dropped) for a {Length}-character {Mode} source.",
            outcome.Boundaries.Count, droppedBlankMarkers, markdown.Length,
            context.IsContainer ? "container" : "embedded-document");

        return outcome;
    }

    /// <summary>
    /// Renders the parent-context block. The container flag is a bool (safe, raw); the title is a snapshot of the
    /// parent's Markdown and the type display name is admin-entered, so both are <see cref="PromptBoundary.WrapField"/>-wrapped;
    /// the parent TypeCode is validated shape and safe raw. Title / type may be absent (best-effort grounding).
    /// </summary>
    private static string BuildParentBlock(SubDocumentDetectionContext context)
    {
        var lines = new List<string>
        {
            context.IsContainer
                ? "- This document is a CONTAINER: a bundle of several independent documents."
                : "- This document is a single concrete document that may EMBED a standalone document (such as an invoice photo inside a contract)."
        };
        if (!string.IsNullOrWhiteSpace(context.ParentTitle))
        {
            lines.Add($"- Title: {PromptBoundary.WrapField(context.ParentTitle)}");
        }
        if (!string.IsNullOrWhiteSpace(context.ParentTypeCode))
        {
            lines.Add($"- TypeCode: {context.ParentTypeCode}");
            if (!string.IsNullOrWhiteSpace(context.ParentTypeDisplayName))
            {
                lines.Add($"  TypeName: {PromptBoundary.WrapField(context.ParentTypeDisplayName)}");
            }
        }

        return string.Join("\n", lines);
    }

    private sealed class SegmentationResponse
    {
        public List<SegmentItem> Segments { get; set; } = new();

        public sealed class SegmentItem
        {
            /// <summary>
            /// The verbatim first line / opening snippet of the span, copied exactly from the Markdown — for an
            /// embedded-image span this first line is the <c>*[Image OCR]*</c> / <c>*[Image OCR p:N]*</c> marker line.
            /// </summary>
            public string StartMarker { get; set; } = default!;

            /// <summary>
            /// <c>true</c> if the span is itself a standalone sub-document (a container constituent, or an embedded
            /// image that is a complete document); <c>false</c> for the parent's own content, a cover / index /
            /// transmittal page, or a figure that is merely an element of the parent.
            /// </summary>
            public bool IsSubDocument { get; set; }
        }
    }
}

public class DocumentSegmentationOutcome
{
    /// <summary>Ordered span boundaries proposed by the LLM; fed to <see cref="MarkdownSlicer.TrySlice"/>.</summary>
    public List<SegmentBoundary> Boundaries { get; } = new();
}

/// <summary>
/// Parent context fed to the unified sub-document detection pass (#371): whether the source is a container bundle
/// (#346) versus a single concrete document that may embed a standalone document (#306), plus best-effort parent
/// title + type for grounding the "standalone vs element-of-parent" judgment (#365). All text fields are nullable /
/// best-effort and PromptBoundary-wrapped at the use site.
/// </summary>
public sealed record SubDocumentDetectionContext(
    bool IsContainer,
    string? ParentTitle,
    string? ParentTypeCode,
    string? ParentTypeDisplayName);
