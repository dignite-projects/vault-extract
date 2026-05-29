using System;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Dignite.Paperbase.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// Creates the observable PipelineRun row before enqueueing the matching background job.
/// A queued job must always carry the PipelineRunId it is expected to execute.
/// </summary>
public class DocumentPipelineJobScheduler : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IBackgroundJobManager _backgroundJobManager;

    public DocumentPipelineJobScheduler(
        IDocumentRepository documentRepository,
        DocumentPipelineRunManager pipelineRunManager,
        IBackgroundJobManager backgroundJobManager)
    {
        _documentRepository = documentRepository;
        _pipelineRunManager = pipelineRunManager;
        _backgroundJobManager = backgroundJobManager;
    }

    /// <summary>
    /// Queues a pipeline run for the document. Optional <paramref name="delay"/> controls
    /// how long the background job waits before being picked up.
    /// </summary>
    public virtual async Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        TimeSpan? delay = null)
    {
        var run = await _pipelineRunManager.QueueAsync(document, pipelineCode);

        await _documentRepository.UpdateAsync(document, autoSave: true);
        await EnqueueAsync(document.Id, pipelineCode, run.Id, delay);

        return run;
    }

    protected virtual Task EnqueueAsync(
        Guid documentId,
        string pipelineCode,
        Guid pipelineRunId,
        TimeSpan? delay = null)
    {
        var effectiveDelay = delay ?? default;
        return pipelineCode switch
        {
            PaperbasePipelines.TextExtraction => _backgroundJobManager.EnqueueAsync(
                new DocumentTextExtractionJobArgs
                {
                    DocumentId = documentId,
                    PipelineRunId = pipelineRunId
                },
                delay: effectiveDelay),
            PaperbasePipelines.Classification => _backgroundJobManager.EnqueueAsync(
                new DocumentClassificationJobArgs
                {
                    DocumentId = documentId,
                    PipelineRunId = pipelineRunId
                },
                delay: effectiveDelay),
            _ => throw new BusinessException(PaperbaseErrorCodes.Pipeline.UnknownCode)
                .WithData("PipelineCode", pipelineCode)
        };
    }
}

public class DocumentPipelineRunAccessor : ITransientDependency
{
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly ILogger<DocumentPipelineRunAccessor> _logger;

    public DocumentPipelineRunAccessor(
        DocumentPipelineRunManager pipelineRunManager,
        ILogger<DocumentPipelineRunAccessor> logger)
    {
        _pipelineRunManager = pipelineRunManager;
        _logger = logger;
    }

    public virtual async Task<DocumentPipelineRun> BeginOrStartAsync(
        Document document,
        Guid? pipelineRunId,
        string pipelineCode)
    {
        if (!pipelineRunId.HasValue)
        {
            return await _pipelineRunManager.StartAsync(document, pipelineCode);
        }

        var run = document.GetRun(pipelineRunId.Value);
        if (run?.PipelineCode == pipelineCode)
        {
            await _pipelineRunManager.BeginAsync(document, run);
            return run;
        }

        if (run != null)
        {
            _logger.LogWarning(
                "Pipeline run {PipelineRunId} on document {DocumentId} belongs to {ActualPipelineCode}, but the job expects {ExpectedPipelineCode}.",
                pipelineRunId.Value, document.Id, run.PipelineCode, pipelineCode);
        }

        var pendingRun = document.GetLatestRun(pipelineCode);
        if (pendingRun?.Status == PipelineRunStatus.Pending)
        {
            _logger.LogWarning(
                "Pipeline run {PipelineRunId} was not found for {PipelineCode} on document {DocumentId}; using latest pending run {ReplacementRunId}.",
                pipelineRunId.Value, pipelineCode, document.Id, pendingRun.Id);
            await _pipelineRunManager.BeginAsync(document, pendingRun);
            return pendingRun;
        }

        _logger.LogWarning(
            "Pipeline run {PipelineRunId} was not found for {PipelineCode} on document {DocumentId}; creating a replacement run.",
            pipelineRunId.Value, pipelineCode, document.Id);
        var replacementRun = await _pipelineRunManager.StartAsync(
            document,
            pipelineCode,
            pipelineRunId.Value);
        return replacementRun;
    }
}
