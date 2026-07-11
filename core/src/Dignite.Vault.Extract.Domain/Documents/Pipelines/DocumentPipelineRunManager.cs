using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Unified entry point for pipeline execution records.
/// Creates runs, drives state transitions, and re-derives Document.LifecycleStatus after every state change.
/// All code that writes pipeline results to Document must go through this service.
/// <para>
/// Design choice: this manager <b>does not query DocumentType</b>. typeCode validation belongs to AppService, and callers are responsible for
/// loading <see cref="DocumentType"/> through <c>IDocumentTypeRepository.FindByTypeCodeAsync</c>
/// and passing it to this manager. This avoids repeated DB reads on hot paths (BackgroundJob already loaded it once; reloading in the manager would be wasteful)
/// and keeps the Domain-layer manager decoupled from upper-layer data access concerns.
/// </para>
/// <para>
/// Since #216, <see cref="DocumentPipelineRun"/> is an independent aggregate root. State transitions are persisted through
/// <see cref="IDocumentPipelineRunRepository"/>, and <see cref="DeriveLifecycleAsync"/> queries the repository to compute latest runs.
/// EF Core LINQ queries hit the DB and do not see Insert/Modify entries that have not yet been flushed in the current UoW, so
/// <see cref="IDocumentPipelineRunRepository.GetLatestRunsByCodesAsync"/> merges change-tracker Local entries inside the repository
/// to produce a post-change view. The derivation logic therefore does not need callers to pass in "the run that was just changed".
/// </para>
/// <para>
/// <b>AttemptNumber concurrency safety</b> (#216 D2 / #239): before the split, UPDATE on the Document main row provided an implicit row lock;
/// after the split, that mutual exclusion is gone. Defense relies on the DB-level <b>unique index</b>
/// <c>(DocumentId, PipelineCode, AttemptNumber)</c> as the hard constraint. It is the only data-integrity guarantee and is
/// <b>fully DB-agnostic</b> (consistent across SqlServer / PostgreSQL / MySQL). <see cref="QueueAsync"/> inserts through
/// <see cref="IDocumentPipelineRunRepository.InsertNewAttemptAsync"/>. The only realistic cause of a key collision is concurrent retry of the same Failed
/// pipeline (operator double-click, two operators, or client timeout resend). The repository translates the provider-agnostic
/// <c>DbUpdateException</c> into <c>RetryInProgress</c>; at that moment, the winner's new run is Pending. The HTTP synchronous path
/// (<c>RetryPipelineAsync</c>) therefore receives a friendly BusinessException instead of a raw 500.
/// <para>
/// Since #239, the application-layer fallback loop "catch key collision -> reread/recompute -> retry" was removed. It was unnecessary
/// because integrity is already protected by the unique index, and it allowed concurrent double-click retry to compute a larger AttemptNumber
/// after the winner committed, insert another run, and enqueue another job, creating duplicate pipeline reruns.
/// Failing immediately on key collision, with the loser receiving RetryInProgress and only one run being created, is the better result.
/// </para>
/// </para>
/// </summary>
public class DocumentPipelineRunManager : DomainService
{
    private readonly IDocumentPipelineRunRepository _runRepo;

    public DocumentPipelineRunManager(IDocumentPipelineRunRepository runRepo)
    {
        _runRepo = runRepo;
    }

    public virtual async Task<DocumentPipelineRun> QueueAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        var latest = await _runRepo.FindLatestByDocumentAndCodeAsync(document.Id, pipelineCode);
        var attemptNumber = (latest?.AttemptNumber ?? 0) + 1;

        var run = new DocumentPipelineRun(
            pipelineRunId ?? GuidGenerator.Create(),
            document.Id,
            document.TenantId,
            pipelineCode,
            attemptNumber);

        run.MarkPending(Clock.Now);

        // Collision on the (DocumentId, PipelineCode, AttemptNumber) unique index -> repository translates to RetryInProgress (see class comments).
        // Unique-constraint collision recognition is centralized in the EF Core layer by catching the provider-agnostic DbUpdateException type,
        // without sniffing messages / SQL Server error codes. The Domain layer no longer references EF Core / SqlClient, preserving cross-DB consistency (#239).
        await _runRepo.InsertNewAttemptAsync(run);
        await DeriveLifecycleAsync(document);
        return run;
    }

    public virtual async Task<DocumentPipelineRun> StartAsync(
        Document document,
        string pipelineCode,
        Guid? pipelineRunId = null)
    {
        var run = await QueueAsync(document, pipelineCode, pipelineRunId);
        await BeginAsync(document, run);
        return run;
    }

    public virtual Task BeginAsync(Document document, DocumentPipelineRun run)
    {
        run.MarkRunning(Clock.Now);
        return PersistAndDeriveAsync(document, run, publishCompletion: false);
    }

    public virtual Task CompleteAsync(Document document, DocumentPipelineRun run)
    {
        run.MarkSucceeded(Clock.Now);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    public virtual Task FailAsync(Document document, DocumentPipelineRun run, string errorMessage)
    {
        run.MarkFailed(Clock.Now, errorMessage);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    /// <summary>
    /// Records text extraction results, writes language + provenance metadata, and completes the run.
    /// <paramref name="markdown"/> is the pipeline's only text payload; digital-native and OCR paths both output Markdown.
    /// Downstream consumers that need plain text project through <see cref="MarkdownStripper.Strip"/>.
    /// <para>
    /// #210: <paramref name="language"/> (ending the write-never dead field) and
    /// <paramref name="extractionMetadata"/> (provenance: provider name + native payload archive manifest;
    /// the caller already archived the raw payload into blob storage during the External phase) are written atomically in the same transaction as Markdown / Title.
    /// Parameters are nullable and have default values so existing callers do not need changes.
    /// </para>
    /// </summary>
    public virtual Task CompleteParseAsync(
        Document document,
        DocumentPipelineRun run,
        string markdown,
        string? title,
        string? language = null,
        DocumentParseMetadata? extractionMetadata = null)
    {
        document.SetMarkdown(markdown);
        document.SetTitle(title);
        document.SetLanguage(language);
        document.SetExtractionMetadata(extractionMetadata);
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// Records classification results and completes the run (high-confidence path).
    /// This path clears <see cref="DocumentReviewReasons.UnresolvedClassification"/> because classification is resolved.
    /// AI classification reason is only written on the low-confidence path (<see cref="CompleteClassificationWithLowConfidenceAsync"/>).
    /// <para>
    /// The caller must pass a loaded <paramref name="typeDef"/> from <c>IDocumentTypeRepository.FindByTypeCodeAsync</c>.
    /// The manager no longer queries the DB; the caller must ensure <c>typeDef.TenantId == document.TenantId</c> for exact single-layer matching.
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
    /// Insufficient classification confidence: completes the run and marks the document for manual review.
    /// Since #284, the AI classification reason is no longer persisted and is only logged.
    /// Run.StatusMessage remains null because <see cref="DocumentPipelineRun.MarkSucceeded"/> does not write StatusMessage,
    /// avoiding confusion with technical error messages.
    /// The review signal is expressed by <see cref="DocumentReviewReasons.UnresolvedClassification"/> and is no longer recorded on the run.
    /// </summary>
    public virtual Task CompleteClassificationWithLowConfidenceAsync(
        Document document,
        DocumentPipelineRun run,
        string? reason = null,
        IReadOnlyList<PipelineRunCandidate>? candidates = null)
    {
        // #284: classification reason is no longer persisted to Document (ClassificationReason was removed); log it only for troubleshooting.
        // The operator-facing "why was it not classified" explanation comes from run candidate types (ClassificationCandidates) + generic frontend copy.
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Logger.LogInformation(
                "Document {DocumentId} routed to classification review (low confidence / unclassifiable): {Reason}",
                document.Id, reason);
        }

        document.RequestClassificationReview();

        if (candidates is { Count: > 0 })
        {
            run.SetProperty(
                PipelineRunExtraPropertyNames.ClassificationCandidates,
                candidates);
        }

        return CompleteAsync(document, run);
    }

    /// <summary>
    /// Records a <b>container</b> classification outcome (#346) and completes the run as succeeded. A container
    /// (a parent bundling several independent documents) runs no type-bound field extraction itself, so
    /// <see cref="Document.MarkAsContainer"/> leaves <see cref="Document.DocumentTypeId"/> null, clears any field
    /// values, and — unlike <see cref="CompleteClassificationWithLowConfidenceAsync"/> — does <b>not</b> set the
    /// blocking <see cref="DocumentReviewReasons.UnresolvedClassification"/> reason. The container is therefore not
    /// sent to the operator review queue and, with both key pipelines succeeded and no blocking reason, derives
    /// straight to <c>Ready</c> (Design A).
    /// <para>
    /// The caller (<c>DocumentClassificationBackgroundJob</c>) must <b>not</b> publish <c>DocumentClassifiedEto</c>
    /// for a container, so <c>FieldExtractionEventHandler</c> never cascades — the race-free way to suppress
    /// extraction is simply never emitting its trigger event.
    /// </para>
    /// </summary>
    public virtual Task CompleteClassificationAsContainerAsync(
        Document document,
        DocumentPipelineRun run)
    {
        document.MarkAsContainer();
        return CompleteAsync(document, run);
    }

    /// <summary>
    /// Operator confirms document type: writes classification result, marks it reviewed, and completes the run. Confidence is fixed at 1.0.
    /// The manual override signal is expressed by <see cref="Document.ReviewDisposition"/> = Confirmed.
    /// This literal is maintained in sync with <c>ClassificationDefaults.ManualClassificationConfidence</c> in Domain.Shared.
    /// Domain does not depend on Abstractions, so it is hard-coded here.
    /// <para>
    /// The caller is responsible for passing the loaded <paramref name="typeDef"/>; the manager no longer queries the DB.
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

    public virtual Task SkipAsync(Document document, DocumentPipelineRun run, string reason)
    {
        run.MarkSkipped(Clock.Now, reason);
        return PersistAndDeriveAsync(document, run, publishCompletion: true);
    }

    /// <summary>
    /// Gets the latest run for this pipeline and validates retryability. Only <see cref="PipelineRunStatus.Failed"/> is retryable.
    /// No run -> <c>Pipeline.NeverRan</c>; Pending/Running -> <c>Pipeline.RetryInProgress</c> as the concurrency guard;
    /// Succeeded/Skipped -> <c>Pipeline.NotRetryable</c>. When validation passes, returns the failed run so the caller can log
    /// <see cref="DocumentPipelineRun.AttemptNumber"/> for audit before calling <see cref="QueueAsync"/> to trigger retry.
    /// <para>
    /// Retry state-machine decisions are domain concerns of the run aggregate and are centralized in the manager, which already owns
    /// <see cref="IDocumentPipelineRunRepository"/>. This keeps AppService from directly querying the run repository (#216 follow-up #6).
    /// PipelineCode validity is input-layer validation and remains with the caller.
    /// </para>
    /// </summary>
    public virtual async Task<DocumentPipelineRun> EnsureRetryableAsync(Guid documentId, string pipelineCode)
    {
        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(documentId, pipelineCode);
        if (latestRun == null)
        {
            throw new BusinessException(VaultExtractErrorCodes.Pipeline.NeverRan)
                .WithData("PipelineCode", pipelineCode);
        }

        switch (latestRun.Status)
        {
            case PipelineRunStatus.Pending:
            case PipelineRunStatus.Running:
                throw new BusinessException(VaultExtractErrorCodes.Pipeline.RetryInProgress)
                    .WithData("PipelineCode", pipelineCode);
            case PipelineRunStatus.Succeeded:
            case PipelineRunStatus.Skipped:
                throw new BusinessException(VaultExtractErrorCodes.Pipeline.NotRetryable)
                    .WithData("PipelineCode", pipelineCode)
                    .WithData("Status", latestRun.Status.ToString());
        }

        return latestRun;
    }

    /// <summary>
    /// Validates that this pipeline currently has no in-progress run. Pending/Running throw <c>RetryInProgress</c> as the concurrency guard.
    /// The key difference from <see cref="EnsureRetryableAsync"/>: Succeeded / Skipped / Failed / never ran all pass.
    /// Used for "rerun on demand" such as #263 "re-recognize" automatic classification, not for "retry failure".
    /// The caller can then safely <see cref="QueueAsync"/> a new attempt with an incremented AttemptNumber that does not collide with historical terminal runs.
    /// </summary>
    public virtual async Task EnsureNotInProgressAsync(Guid documentId, string pipelineCode)
    {
        var latestRun = await _runRepo.FindLatestByDocumentAndCodeAsync(documentId, pipelineCode);
        if (latestRun is { Status: PipelineRunStatus.Pending or PipelineRunStatus.Running })
        {
            throw new BusinessException(VaultExtractErrorCodes.Pipeline.RetryInProgress)
                .WithData("PipelineCode", pipelineCode);
        }
    }

    /// <summary>
    /// Shared tail of every state transition: persist the run via repo, optionally fire the run-completed
    /// LocalEvent, then re-derive Document.LifecycleStatus. BeginAsync skips the event (run is just starting,
    /// not completing); Complete/Fail/Skip all publish it.
    /// </summary>
    private async Task PersistAndDeriveAsync(
        Document document,
        DocumentPipelineRun run,
        bool publishCompletion)
    {
        await _runRepo.UpdateAsync(run);

        if (publishCompletion)
        {
            run.PublishRunCompletedEvent();
        }

        await DeriveLifecycleAsync(document);
    }

    /// <summary>
    /// Re-derives <see cref="Document.LifecycleStatus"/> from the current pipeline runs + review reasons <b>without
    /// changing any run</b> (#411). Operator review-resolution paths that only mutate a blocking review reason — e.g.
    /// <c>AllowDuplicateAsync</c> clearing <see cref="DocumentReviewReasons.DuplicateSuspected"/> — call this so the
    /// Ready gate is re-evaluated and the document can transition to Ready (emitting <c>DocumentReadyEto</c>) when no
    /// other blocking reason remains. The caller persists the document afterward; <see cref="DeriveLifecycleAsync"/>
    /// only mutates in-memory lifecycle + queues the transition LocalEvent.
    /// </summary>
    public virtual Task ReDeriveLifecycleAsync(Document document) => DeriveLifecycleAsync(document);

    /// <summary>
    /// Derives Document.LifecycleStatus from the latest runs of all key pipelines.
    /// <para>
    /// Latest runs are provided by <see cref="IDocumentPipelineRunRepository.GetLatestRunsByCodesAsync"/>. That repository already
    /// merges change-tracker entities not yet flushed in the current UoW (the EF Core implementation peeks Local entries; the in-memory fake
    /// naturally sees them because it holds run references). Therefore consuming repository results here gives the post-change view directly,
    /// without requiring callers to pass "the run that was just changed".
    /// </para>
    /// </summary>
    protected virtual async Task DeriveLifecycleAsync(Document document)
    {
        var latestRuns = await _runRepo.GetLatestRunsByCodesAsync(
            document.Id, VaultExtractPipelines.KeyPipelines);

        var derivedStatus = DocumentLifecycleStatus.Processing;
        var allSucceeded = true;

        foreach (var pipelineCode in VaultExtractPipelines.KeyPipelines)
        {
            // #411: field-extraction is a key pipeline so the duplicate check can gate Ready, but a container runs
            // no field extraction (it holds no single type's fields) — exempt it from that requirement so it still
            // reaches Ready lifecycle (its DocumentReadyEto is separately suppressed in DocumentReadyEventHandler).
            if (document.IsContainer && pipelineCode == VaultExtractPipelines.FieldExtraction)
            {
                continue;
            }

            if (!latestRuns.TryGetValue(pipelineCode, out var latestRun))
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

        // #284: Ready gate is "no blocking review reason" (ReviewReasonPolicy.Blocking is the single declaration
        // point). #510: split the not-Ready availability appearance in two. A document that carries a blocking
        // reason is PendingReview — it has run as far as it can but is withheld from Ready waiting on the operator
        // (low-confidence classification, suspected duplicate, oversized-for-field-extraction). That is distinct
        // from Processing, which now means only "a key pipeline is still running / has not started" (no blocking
        // reason yet). Both withhold downstream release identically — only the transition to Ready fires
        // DocumentReadyEto — so PendingReview changes the operator-facing status without touching the egress gate.
        // Failed still dominates both (a failed key pipeline, or an operator rejection via RejectReview).
        if (derivedStatus != DocumentLifecycleStatus.Failed)
        {
            if (ReviewReasonPolicy.HasBlocking(document.ReviewReasons))
            {
                derivedStatus = DocumentLifecycleStatus.PendingReview;
            }
            else if (allSucceeded)
            {
                derivedStatus = DocumentLifecycleStatus.Ready;
            }
        }

        document.TransitionLifecycle(derivedStatus);
    }
}
