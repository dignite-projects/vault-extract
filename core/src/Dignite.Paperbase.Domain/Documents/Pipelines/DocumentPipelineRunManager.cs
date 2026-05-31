using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Services;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// 流水线执行记录的统一入口。
/// 负责创建 Run、驱动状态流转、在每次状态变化后重新派生 Document.LifecycleStatus。
/// 所有向 Document 写入流水线结果的代码都必须通过此服务。
/// <para>
/// 设计选择：本 manager <b>不查 DB</b>——typeCode 校验责任在 AppService（调用方负责
/// 通过 <c>IDocumentTypeRepository.FindByTypeCodeAsync</c> 加载 <see cref="DocumentType"/>
/// 并传给本 manager）。这避免热路径重复查 DB（BackgroundJob 已 load 一次，manager 再查一次浪费），
/// 并让 Domain 层 manager 纯净不依赖 Repository。
/// </para>
/// </summary>
public class DocumentPipelineRunManager : DomainService
{
    public DocumentPipelineRunManager()
    {
    }

    public virtual Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        var attemptNumber = document.PipelineRuns
            .Where(r => r.PipelineCode == pipelineCode)
            .Select(r => r.AttemptNumber)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var run = new DocumentPipelineRun(
            pipelineRunId ?? GuidGenerator.Create(),
            document.Id,
            document.TenantId,
            pipelineCode,
            attemptNumber);

        run.MarkPending(Clock.Now);
        document.AddPipelineRun(run);

        DeriveLifecycle(document);

        return Task.FromResult(run);
    }

    public virtual async Task<DocumentPipelineRun> StartAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        var run = await QueueAsync(document, pipelineCode, pipelineRunId);
        run.MarkRunning(Clock.Now);
        DeriveLifecycle(document);
        return run;
    }

    public virtual Task BeginAsync(Document document, DocumentPipelineRun run)
    {
        run.MarkRunning(Clock.Now);
        DeriveLifecycle(document);
        return Task.CompletedTask;
    }

    public virtual Task CompleteAsync(
        Document document,
        DocumentPipelineRun run)
    {
        run.MarkSucceeded(Clock.Now);

        document.PublishPipelineRunCompletedEvent(new DocumentPipelineRunCompletedEvent(
            document.Id,
            run.PipelineCode,
            run.Status));

        DeriveLifecycle(document);

        return Task.CompletedTask;
    }

    public virtual Task FailAsync(
        Document document,
        DocumentPipelineRun run,
        string errorMessage)
    {
        run.MarkFailed(Clock.Now, errorMessage);

        document.PublishPipelineRunCompletedEvent(new DocumentPipelineRunCompletedEvent(
            document.Id,
            run.PipelineCode,
            run.Status));

        DeriveLifecycle(document);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录文本提取结果、写入语言 + provenance 元数据并完成 Run。
    /// <paramref name="markdown"/> 是流水线唯一的文本载荷（数字版与 OCR 路径都已统一输出 Markdown）；
    /// 下游需要纯文本时通过 <see cref="MarkdownStripper.Strip"/> 投影。
    /// <para>
    /// #210：<paramref name="language"/>（终结 write-never 死字段）与
    /// <paramref name="extractionMetadata"/>（provenance：provider 名 + 原生 payload 归档 manifest，
    /// 原始 payload 已由调用方在 External 段归档进 blob）与 Markdown / Title 同事务原子写入。
    /// 参数可空且有默认值——既有调用方无需改动。
    /// </para>
    /// </summary>
    public virtual Task CompleteTextExtractionAsync(
        Document document,
        DocumentPipelineRun run,
        string markdown,
        string? title,
        string? language = null,
        DocumentTextExtractionMetadata? extractionMetadata = null)
    {
        document.SetMarkdown(markdown);
        document.SetTitle(title);
        document.SetLanguage(language);
        document.SetExtractionMetadata(extractionMetadata);
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 记录分类结果并完成 Run（高置信度路径）。
    /// <see cref="Document.ClassificationReason"/> 在此路径下固定为 null；
    /// AI 的分类理由仅在低置信度路径（<see cref="CompleteClassificationWithLowConfidenceAsync"/>）写入。
    /// <para>
    /// 调用方负责传入已加载的 <paramref name="typeDef"/>（来自 <c>IDocumentTypeRepository.FindByTypeCodeAsync</c>），
    /// manager 不再查 DB；调用方必须确保 <c>typeDef.TenantId == document.TenantId</c>（单层精确匹配）。
    /// </para>
    /// </summary>
    public virtual Task CompleteClassificationAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentType typeDef,
        double confidenceScore)
    {
        Check.NotNull(typeDef, nameof(typeDef));
        document.ApplyAutomaticClassificationResult(typeDef.Id, confidenceScore);
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 分类置信度不足：完成 Run 并将文档标记为待人工审核。
    /// <see cref="Document.ClassificationReason"/> 写入 AI 的分类理由（reason）；
    /// Run.StatusMessage 保持 null（<see cref="DocumentPipelineRun.MarkSucceeded"/> 不写 StatusMessage），
    /// 避免与技术错误信息混淆。
    /// 置信度信号由 <see cref="Document.ReviewStatus"/> = PendingReview 表达，不再记录在 Run 上。
    /// </summary>
    public virtual Task CompleteClassificationWithLowConfidenceAsync(
        Document document,
        DocumentPipelineRun run,
        string? reason = null,
        IReadOnlyList<PipelineRunCandidate>? candidates = null)
    {
        document.RequestClassificationReview(reason);

        if (candidates is { Count: > 0 })
        {
            run.SetProperty(
                PipelineRunExtraPropertyNames.ClassificationCandidates,
                candidates);
        }

        return CompleteAsync(document, run);
    }

    /// <summary>
    /// 人工确认文档类型：写入分类结果、标记已审核、完成 Run。置信度固定为 1.0。
    /// 人工覆盖信号由 <see cref="Document.ReviewStatus"/> = Reviewed 表达。
    /// 该字面量与 Domain.Shared 层 <c>ClassificationDefaults.ManualClassificationConfidence</c>
    /// 同步维护（Domain 不依赖 Abstractions，故此处硬编码）。
    /// <para>
    /// 调用方负责传入已加载的 <paramref name="typeDef"/>；manager 不再查 DB。
    /// </para>
    /// </summary>
    public virtual Task CompleteManualClassificationAsync(
        Document document,
        DocumentPipelineRun run,
        DocumentType typeDef)
    {
        Check.NotNull(typeDef, nameof(typeDef));
        document.ConfirmClassification(typeDef.Id);
        return CompleteAsync(document, run);
    }

    public virtual Task SkipAsync(
        Document document,
        DocumentPipelineRun run,
        string reason)
    {
        run.MarkSkipped(Clock.Now, reason);

        document.PublishPipelineRunCompletedEvent(new DocumentPipelineRunCompletedEvent(
            document.Id,
            run.PipelineCode,
            run.Status));

        DeriveLifecycle(document);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 根据所有关键流水线的最新 Run 派生 Document.LifecycleStatus。
    /// </summary>
    protected virtual void DeriveLifecycle(Document document)
    {
        var derivedStatus = DocumentLifecycleStatus.Processing;

        var allSucceeded = true;

        foreach (var pipelineCode in PaperbasePipelines.KeyPipelines)
        {
            var latestRun = document.GetLatestRun(pipelineCode);

            if (latestRun == null)
            {
                allSucceeded = false;
                continue;
            }

            if (latestRun.Status == PipelineRunStatus.Failed)
            {
                derivedStatus = DocumentLifecycleStatus.Failed;
                allSucceeded = false;
                break;
            }

            if (latestRun.Status != PipelineRunStatus.Succeeded)
            {
                allSucceeded = false;
            }
        }

        if (derivedStatus != DocumentLifecycleStatus.Failed &&
            allSucceeded &&
            document.DocumentTypeId.HasValue)
        {
            derivedStatus = DocumentLifecycleStatus.Ready;
        }

        document.TransitionLifecycle(derivedStatus);
    }
}
