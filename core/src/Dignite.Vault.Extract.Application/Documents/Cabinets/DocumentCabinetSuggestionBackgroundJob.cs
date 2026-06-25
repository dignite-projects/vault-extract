using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Threading;
using Volo.Abp.Uow;

namespace Dignite.Vault.Extract.Documents.Cabinets;

/// <summary>
/// Background job for "blank cabinet AI fallback selection" (#265): fanned out once by
/// <c>DocumentParseBackgroundJob</c> after text extraction succeeds and Markdown is ready.
/// It is independent, one-shot, and best-effort. To preserve the #194 orthogonality guardrail, this is
/// the only AI write point for <see cref="Document.CabinetId"/>; it does not create a
/// <c>DocumentPipelineRun</c>, does not participate in the Ready gate, and is not retryable.
/// Classification / field extraction pipelines still do not read or write CabinetId.
/// <para>
/// It fills CabinetId only when the field is blank (operator choice wins; Begin + Complete double
/// gating, with Complete rechecking the #257 reassignment race). The three-stage UoW shape follows
/// background-jobs.md: Begin loads + gates + fetches candidates, External runs the LLM without a UoW,
/// and Complete rechecks + writes back.
/// </para>
/// <para>
/// <b>Fail-open swallows every exception, including cancellation</b>: this job is not a PipelineRun
/// and is not retryable, so the <see cref="ExecuteAsync"/> catch deliberately has no
/// <c>when (ex is not OperationCanceledException)</c> filter. Otherwise provider per-call timeouts
/// (<see cref="TaskCanceledException"/> derives from <see cref="OperationCanceledException"/>) would
/// escape and trigger an ABP retry storm, violating the "not retryable" contract. Shutdown promptness
/// is handled by the ambient cancellation token, which in-flight LLM calls observe; the cancellation
/// exception itself is swallowed, leaving the document uncategorized.
/// </para>
/// </summary>
[BackgroundJobName("Extract.DocumentCabinetSuggestion")]
public class DocumentCabinetSuggestionBackgroundJob
    : AsyncBackgroundJob<DocumentCabinetSuggestionJobArgs>, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly CabinetSuggestionWorkflow _workflow;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ExtractBehaviorOptions _options;
    // ABP BackgroundJobExecuter pushes the job cancellation token into ambient state before calling
    // ExecuteAsync. The default worker source is the host shutdown token, so slow external work such
    // as LLM calls can cancel promptly during shutdown, matching DocumentParseBackgroundJob.
    private readonly ICancellationTokenProvider _cancellationTokenProvider;

    public DocumentCabinetSuggestionBackgroundJob(
        IDocumentRepository documentRepository,
        ICabinetRepository cabinetRepository,
        CabinetSuggestionWorkflow workflow,
        IUnitOfWorkManager unitOfWorkManager,
        ICurrentTenant currentTenant,
        IOptions<ExtractBehaviorOptions> options,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        _documentRepository = documentRepository;
        _cabinetRepository = cabinetRepository;
        _workflow = workflow;
        _unitOfWorkManager = unitOfWorkManager;
        _currentTenant = currentTenant;
        _options = options.Value;
        _cancellationTokenProvider = cancellationTokenProvider;
    }

    public override async Task ExecuteAsync(DocumentCabinetSuggestionJobArgs args)
    {
        try
        {
            var workItem = await PrepareAsync(args.DocumentId);
            if (workItem == null)
            {
                // Self-gated out (operator already selected, no Markdown, or no candidate cabinets):
                // end silently and leave the document uncategorized.
                return;
            }

            var outcome = await SuggestAsync(workItem);

            await ApplyAsync(args.DocumentId, outcome);
        }
        catch (Exception ex)
        {
            // Fail-open: swallow everything, including cancellation. See the class comment for why no
            // OperationCanceledException filter is used and why this avoids ABP retry storms.
            Logger.LogWarning(ex,
                "AI cabinet suggestion failed for document {DocumentId}; leaving it uncategorized.",
                args.DocumentId);
        }
    }

    /// <summary>Begin stage (short UoW): load the document, self-gate, and fetch current-layer candidate cabinets. Returns <c>null</c> when gated out.</summary>
    protected virtual async Task<CabinetSuggestionWorkItem?> PrepareAsync(Guid documentId)
    {
        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

        // Guardrail 2: an operator already selected a cabinet, or a previous pass filled one. Human
        // choice wins and AI must not overwrite it. Cabinet selection is content-based (#265), so
        // without Markdown there is nothing to classify.
        if (document.CabinetId.HasValue || string.IsNullOrEmpty(document.Markdown))
        {
            await uow.CompleteAsync();
            return null;
        }

        // Match candidates to the document tenant as a single layer via the ambient IMultiTenant
        // filter, then use stable name ordering and truncate.
        List<Cabinet> candidates;
        using (_currentTenant.Change(document.TenantId))
        {
            var all = await _cabinetRepository.GetListAsync();
            candidates = all
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Take(_options.MaxCabinetsInSuggestionPrompt)
                .ToList();

            // Cabinet count exceeds the prompt cap. Dropped cabinets are lexicographically later
            // because there is no priority model, so this is the least-bad deterministic choice; the
            // correct cabinet may be outside the truncated set, so log a warning for operations.
            if (all.Count > _options.MaxCabinetsInSuggestionPrompt)
            {
                Logger.LogWarning(
                    "Document {DocumentId} layer has {CabinetCount} cabinets, exceeding the suggestion cap {Cap}; "
                    + "candidates beyond the cap are excluded from the prompt.",
                    document.Id, all.Count, _options.MaxCabinetsInSuggestionPrompt);
            }
        }

        await uow.CompleteAsync();

        if (candidates.Count == 0)
        {
            return null;
        }

        return new CabinetSuggestionWorkItem(document.Id, document.TenantId, document.Markdown, candidates);
    }

    /// <summary>External stage (no UoW): ask the LLM to choose a cabinet. Keep the ambient tenant aligned with candidate assembly as a defense against future workflow-side queries, and pass the ambient job cancellation token so LLM calls can cancel promptly during shutdown.</summary>
    protected virtual async Task<CabinetSuggestionOutcome> SuggestAsync(CabinetSuggestionWorkItem workItem)
    {
        using (_currentTenant.Change(workItem.TenantId))
        {
            return await _workflow.RunAsync(
                workItem.Candidates, workItem.Markdown, _cancellationTokenProvider.Token);
        }
    }

    /// <summary>Complete stage (short UoW): apply the threshold decision, recheck races, and write back CabinetId.</summary>
    protected virtual async Task ApplyAsync(Guid documentId, CabinetSuggestionOutcome outcome)
    {
        // No choice (no candidates, LLM abstained, or index out of range) or confidence below the
        // threshold means no write; keep the document uncategorized because #265 prefers abstaining
        // over a low-quality assignment.
        if (outcome.CabinetId is not { } cabinetId
            || outcome.Confidence < _options.MinCabinetSuggestionConfidence)
        {
            return;
        }

        using var uow = _unitOfWorkManager.Begin(requiresNew: true);

        var document = await _documentRepository.GetAsync(documentId, includeDetails: false);

        // Guardrail 2 recheck: an operator may have reassigned the document while the LLM was running
        // (#257). Human choice wins, so do not overwrite it.
        if (document.CabinetId.HasValue)
        {
            await uow.CompleteAsync();
            return;
        }

        // Recheck that the cabinet still exists in the current layer. It may have been deleted while
        // the LLM was running, and we must not write a dangling CabinetId.
        using (_currentTenant.Change(document.TenantId))
        {
            var cabinet = await _cabinetRepository.FindAsync(cabinetId);
            if (cabinet == null)
            {
                await uow.CompleteAsync();
                return;
            }
        }

        document.SetCabinet(cabinetId);
        await _documentRepository.UpdateAsync(document, autoSave: true);

        await uow.CompleteAsync();
    }

    public sealed record CabinetSuggestionWorkItem(
        Guid DocumentId,
        Guid? TenantId,
        string Markdown,
        IReadOnlyList<Cabinet> Candidates);
}

public class DocumentCabinetSuggestionJobArgs
{
    public Guid DocumentId { get; set; }
}
