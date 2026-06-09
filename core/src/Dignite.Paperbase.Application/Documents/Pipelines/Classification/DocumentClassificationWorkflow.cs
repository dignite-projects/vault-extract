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

namespace Dignite.Paperbase.Documents.Pipelines.Classification;

/// <summary>
/// 文档分类 Workflow（MAF ChatClientAgent + 结构化输出）。
/// </summary>
public class DocumentClassificationWorkflow : ITransientDependency
{
    /// <summary>
    /// Structured-output (<c>RunAsync&lt;ClassificationResponse&gt;</c>), tool-free,
    /// prompt-unique call — routed through the dedicated keyed client
    /// (<see cref="PaperbaseAIConsts.StructuredChatClientKey"/>) so traces stay clean
    /// and hosts can optionally point classification at a smaller / cheaper model than
    /// the main agentic chat. See <c>docs/ai-provider.md</c> keyed-clients table.
    /// </summary>
    private readonly IChatClient _chatClient;
    private readonly IPromptProvider _promptProvider;
    private readonly PaperbaseAIBehaviorOptions _options;

    public ILogger<DocumentClassificationWorkflow> Logger { get; set; }
        = NullLogger<DocumentClassificationWorkflow>.Instance;

    public DocumentClassificationWorkflow(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<PaperbaseAIBehaviorOptions> options,
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

        // 候选集排序与数量上限由调用方（DocumentClassificationBackgroundJob）决定。
        // 分类只需文档前段语义即可判型，故按 MaxTextLengthPerExtraction 截断前部（与字段抽取喂全文有意分化）。
        var truncatedText = markdown;
        if (markdown.Length > _options.MaxTextLengthPerExtraction)
        {
            // 截断会丢弃文档尾部，关键字段若位于尾部将无法分类——运营侧需要可见性。
            Logger.LogWarning(
                "Classification input truncated from {OriginalLength} to {TruncatedLength} characters; key fields beyond the cutoff will be missed.",
                markdown.Length, _options.MaxTextLengthPerExtraction);
            truncatedText = TruncateAtCharBoundary(markdown, _options.MaxTextLengthPerExtraction);
        }

        // 字段架构 v2：DocumentType.DisplayName / Description 是 DB-resolved string，
        // Host admin / 租户 admin 通过 admin UI 直接输入 string——是用户控制文本，
        // 必须经 PromptBoundary.WrapField 包裹（CLAUDE.md "## 安全约定 / PromptBoundary"），
        // 防止恶意 admin 通过 DisplayName / Description 注入指令（"Ignore previous instructions..."）。
        // TypeCode 已经走实体层 ValidateTypeCode 强校验为 <owner>.<sub-type> 形式，安全。
        // Description（#262）是可选分类辅助文本：仅当非空时追加一行，帮助 LLM 判型——不做任何内容二次加工。
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
                Name = "PaperbaseDocumentClassifier",
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

        // LLM 偶发返回百分制置信度（如 99.9）或真正非法值（NaN / <0 / >100）。
        // 百分制先归一化到 0..1；真正非法值按"无可信结论"处理：
        // typeCode 置 null、confidence 置 0，由 BackgroundJob 走 LowConfidence 分支
        // 进待人工审核队列（置 UnresolvedClassification 原因），避免 Document.ApplyAutomaticClassificationResult 的
        // Check.Range 抛异常导致整条 PipelineRun 翻成 Failed。
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

        // Reason（LLM 给的分类理由）随 outcome 透传给 BackgroundJob 仅作日志 / 诊断——#284 起不再持久化到 Document
        // （旧 ClassificationReason 字段已删）：高置信度路径忽略；低置信度路径仅记 log。操作员"为什么没分对"由
        // DocumentPipelineRun 的候选类型（ClassificationCandidates）+ 前端通用文案承载。
        // Run.StatusMessage 在两条路径下均不写入（MarkSucceeded 不接受 statusMessage），避免与技术错误信息混淆。
        var outcome = new DocumentClassificationOutcome
        {
            TypeCode = typeCode,
            ConfidenceScore = confidenceScore,
            Reason = parsed?.Reason
        };

        if (parsed?.Candidates != null)
        {
            foreach (var c in parsed.Candidates)
            {
                // 候选项的 confidence 仅用于 UI 展示与 Run 持久化（PipelineRunCandidate 是纯
                // record，不做 Check.Range），越界不会破坏聚合根；这里 Clamp 保证展示侧不出
                // 现 1.5 之类的脏数据。
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

    // 按 UTF-16 码元上限截断，但不切断代理对：末位若是高位代理（其低位已被切掉），一并丢弃，
    // 避免半个码点在 UTF-8 编码送 LLM 时退化成 U+FFFD。截断点已在被丢弃的文档尾部，多退一个 char 无影响。
    // internal 便于 Application.Tests 直接验证边界逻辑（与上面置信度 helper 同源）。
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
    public List<PipelineRunCandidate> Candidates { get; } = new();
}
