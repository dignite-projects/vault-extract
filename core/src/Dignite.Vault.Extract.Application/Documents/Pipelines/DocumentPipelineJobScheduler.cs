using System;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Pipelines;

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
    /// <para>
    /// <paramref name="expectedEventTypeCode"/> (#527 §8) is the field-extraction cascade's stale-reclassify
    /// early-exit hint: when the classification stage schedules the cascade field-extraction run transactionally, it
    /// forwards the just-assigned <c>DocumentType.TypeCode</c> so a queued run whose type has since changed can
    /// short-circuit in <see cref="FieldExtractionService"/> phase 1. It is only meaningful for
    /// <see cref="VaultExtractPipelines.FieldExtraction"/>; other pipeline codes ignore it.
    /// </para>
    /// </summary>
    public virtual async Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        TimeSpan? delay = null,
        string? expectedEventTypeCode = null)
    {
        var run = await _pipelineRunManager.QueueAsync(document, pipelineCode);

        await _documentRepository.UpdateAsync(document, autoSave: true);
        await EnqueueAsync(document.Id, document.TenantId, pipelineCode, run.Id, delay, expectedEventTypeCode);

        return run;
    }

    protected virtual Task EnqueueAsync(
        Guid documentId,
        Guid? tenantId,
        string pipelineCode,
        Guid pipelineRunId,
        TimeSpan? delay = null,
        string? expectedEventTypeCode = null)
    {
        var effectiveDelay = delay ?? default;
        return pipelineCode switch
        {
            VaultExtractPipelines.Parse => _backgroundJobManager.EnqueueAsync(
                new DocumentParseJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId
                },
                delay: effectiveDelay),
            VaultExtractPipelines.Classification => _backgroundJobManager.EnqueueAsync(
                new DocumentClassificationJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId
                },
                delay: effectiveDelay),
            VaultExtractPipelines.FieldExtraction => _backgroundJobManager.EnqueueAsync(
                new DocumentFieldExtractionJobArgs
                {
                    DocumentId = documentId,
                    TenantId = tenantId,
                    PipelineRunId = pipelineRunId,
                    ExpectedEventTypeCode = expectedEventTypeCode
                },
                delay: effectiveDelay),
            _ => throw new BusinessException(VaultExtractErrorCodes.Pipeline.UnknownCode)
                .WithData("PipelineCode", pipelineCode)
        };
    }
}

public class DocumentPipelineRunAccessor : ITransientDependency
{
    private readonly DocumentPipelineRunManager _pipelineRunManager;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly ILogger<DocumentPipelineRunAccessor> _logger;

    public DocumentPipelineRunAccessor(
        DocumentPipelineRunManager pipelineRunManager,
        IDocumentPipelineRunRepository runRepository,
        ILogger<DocumentPipelineRunAccessor> logger)
    {
        _pipelineRunManager = pipelineRunManager;
        _runRepository = runRepository;
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

        // #216: after PipelineRun became an independent aggregate root, locate it directly by runId
        // through the repository instead of Document.GetRun.
        var run = await _runRepository.FindAsync(pipelineRunId.Value);
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

        var pendingRun = await _runRepository.FindLatestByDocumentAndCodeAsync(document.Id, pipelineCode);
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
