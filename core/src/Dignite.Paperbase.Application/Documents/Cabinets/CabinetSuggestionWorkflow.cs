using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 「留空 AI 兜底选柜」Workflow（#265）：从当前层候选柜里为文档挑一个最贴合的，无清晰匹配则弃选。
/// 正交于内容 pipeline——不建 PipelineRun、不进 Ready 闸门（#194 落地）。安全约定见 llm-call-anti-patterns.md。
/// </summary>
public class CabinetSuggestionWorkflow : ITransientDependency
{
    /// <summary>与分类同走 <see cref="PaperbaseAIConsts.StructuredChatClientKey"/> keyed client（structured-output、tool-free）——host 可指向更小 / 更便宜的模型。</summary>
    private readonly IChatClient _chatClient;
    private readonly PaperbaseAIBehaviorOptions _options;

    public ILogger<CabinetSuggestionWorkflow> Logger { get; set; }
        = NullLogger<CabinetSuggestionWorkflow>.Instance;

    public CabinetSuggestionWorkflow(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<PaperbaseAIBehaviorOptions> options)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    /// <summary>编译期常量 system instructions（防注入，不拼接运行时字符串）；让 LLM 选一个编号或弃选（宁缺毋滥）。</summary>
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
    /// 为 <paramref name="markdown"/> 从候选柜里挑一个；<see cref="CabinetSuggestionOutcome.CabinetId"/> 为 <c>null</c> = 弃选
    /// （无候选 / LLM 弃选 / 编号越界）。置信度阈值由调用方裁决，本方法只解析 + 映射。
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

        // 选柜只需文档前段语义即可判归属，故按 MaxTextLengthPerExtraction 截断前部（与分类同策略）。
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
                Name = "PaperbaseCabinetSuggester",
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
    /// 候选柜格式化为 1-based 编号列表喂 LLM——用编号而非 Guid / Name 回显（避免 LLM 复制 GUID 出错，且天然抗注入：
    /// 只能在预载候选内选）。Name / Description 都是用户控制文本，必须 <c>PromptBoundary.WrapField</c> 包裹；
    /// 空 Description 只给 Name（镜像 <c>DocumentClassificationWorkflow</c> 的 description-optional 拼接）。
    /// internal 便于 Application.Tests 直接验证拼接格式。
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
    /// LLM 的 <see cref="CabinetSuggestionResponse"/> → <see cref="CabinetSuggestionOutcome"/>：1-based 编号映射回
    /// 候选 <see cref="Cabinet.Id"/>，越界 / null → 弃选；置信度 clamp 到 0..1。internal 便于 Tests 验证边界。
    /// </summary>
    internal CabinetSuggestionOutcome ResolveOutcome(
        CabinetSuggestionResponse? parsed,
        IReadOnlyList<Cabinet> candidates)
    {
        if (parsed?.CabinetIndex is not { } index)
        {
            return CabinetSuggestionOutcome.None;
        }

        // 1-based 编号；越界（含 LLM 返回 0 / 负数 / 超过候选数）→ 弃选，不写脏柜。
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

    // 按 UTF-16 码元上限截断，但不切断代理对（与 DocumentClassificationWorkflow.TruncateAtCharBoundary 同源）。
    // internal 便于 Application.Tests 直接验证边界逻辑。
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
        /// <summary>1-based 候选编号；<c>null</c> 表示 LLM 弃选（无清晰匹配）。</summary>
        public int? CabinetIndex { get; set; }

        public double Confidence { get; set; }
    }
}

/// <summary>
/// 选柜结果。<see cref="CabinetId"/> 为 <c>null</c> = 弃选（无候选 / LLM 弃选 / 编号越界），调用方据此保持「未归类」。
/// </summary>
public sealed class CabinetSuggestionOutcome
{
    public Guid? CabinetId { get; init; }

    public double Confidence { get; init; }

    public static CabinetSuggestionOutcome None { get; } = new() { CabinetId = null, Confidence = 0d };
}
