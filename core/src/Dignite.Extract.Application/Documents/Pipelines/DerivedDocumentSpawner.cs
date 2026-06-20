using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Timing;
using Volo.Abp.Uow;

namespace Dignite.Extract.Documents.Pipelines;

/// <summary>
/// Shared sink for spawning a derived <see cref="Document"/> from one constituent of a source document — the common
/// short-UoW protocol behind figure routing (#306) and born-digital container segmentation (#346), extracted so the
/// two jobs no longer duplicate it (#358). In one atomic UoW it: re-checks the candidate is still claimable, inserts
/// the derived document, marks the candidate spawned, publishes <see cref="DocumentUploadedEto"/>, and queues the
/// derived document's text-extraction pipeline.
/// <para>
/// The caller owns its constituent-specific work: materializing the derived-document blob (the <see cref="FileOrigin"/>
/// it passes in already points at a written blob) and the candidate aggregate's reload + status mutation (supplied as
/// <paramref name="reloadClaimable"/> / <paramref name="markSpawned"/>, both run inside this UoW under the source's
/// tenant). This sink touches no blob storage, so blob lifecycle (write + any cleanup) stays entirely with the caller.
/// </para>
/// <para>
/// <b>Idempotent + self-healing.</b> The derived insert is <c>autoSave: true</c>, so a concurrent double-spawn trips
/// the unique <c>(OriginDocumentId, OriginConstituentKey)</c> index right here and the exception propagates to the
/// caller's per-item isolation (the job is retried; on retry the candidate is terminal and skipped). When
/// <paramref name="reloadClaimable"/> reports the candidate is no longer claimable (a concurrent run already routed it,
/// or it was removed / reverted), the spawn is aborted cleanly — nothing is inserted and <c>null</c> is returned.
/// </para>
/// <para>
/// <b>UoW discipline</b> (background-jobs.md): the caller's blob IO and any gate/LLM work run outside any UoW; only
/// this short complete-phase UoW is opened here.
/// </para>
/// </summary>
public class DerivedDocumentSpawner : ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly IDistributedEventBus _distributedEventBus;
    private readonly ICurrentTenant _currentTenant;
    private readonly IClock _clock;
    private readonly IGuidGenerator _guidGenerator;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DerivedDocumentSpawner(
        IDocumentRepository documentRepository,
        DocumentPipelineJobScheduler pipelineJobScheduler,
        IDistributedEventBus distributedEventBus,
        ICurrentTenant currentTenant,
        IClock clock,
        IGuidGenerator guidGenerator,
        IUnitOfWorkManager unitOfWorkManager)
    {
        _documentRepository = documentRepository;
        _pipelineJobScheduler = pipelineJobScheduler;
        _distributedEventBus = distributedEventBus;
        _currentTenant = currentTenant;
        _clock = clock;
        _guidGenerator = guidGenerator;
        _unitOfWorkManager = unitOfWorkManager;
    }

    /// <summary>
    /// Complete phase (short UoW): insert the derived document for one constituent, mark its candidate spawned,
    /// publish <see cref="DocumentUploadedEto"/>, and queue text extraction — atomically. Returns the spawned
    /// document id, or <c>null</c> when <paramref name="reloadClaimable"/> reports the candidate is no longer
    /// claimable (the caller decides what to do with the blob it already wrote, per its own naming scheme).
    /// </summary>
    /// <typeparam name="TCandidate">
    /// The candidate ledger aggregate. After #371 unified the figure + born-digital ledgers there is exactly one
    /// caller, passing <c>DocumentSegment</c>; the generic is <b>deliberately retained</b> as a decoupling seam — it
    /// keeps this sink (in <c>Pipelines</c>) from referencing the ledger type (in <c>Documents.Segments</c>) or
    /// hard-coding a reload/mark protocol, so the candidate's claim + spawn mutation enter through the two delegates
    /// (<paramref name="reloadClaimable"/> / <paramref name="markSpawned"/>). Not dead generality — it is the boundary
    /// that kept the shared #358 spawn protocol caller-agnostic.
    /// </typeparam>
    /// <param name="sourceDocumentId">The source document the constituent belongs to (the derived doc's <c>OriginDocumentId</c>).</param>
    /// <param name="tenantId">The source/derived tenant; the UoW and the delegates run under this tenant.</param>
    /// <param name="constituentKey">The content-derived key (the derived doc's <c>OriginConstituentKey</c>): SHA-256 of the clean slice text.</param>
    /// <param name="fileOrigin">The derived document's file origin, or <c>null</c> for sub-documents with no source blob (figure spans seeded from segment SliceText).</param>
    /// <param name="reloadClaimable">Reloads the candidate inside the UoW; returns it when still claimable, or <c>null</c> to abort.</param>
    /// <param name="markSpawned">Marks the (reloaded) candidate spawned with the derived document id and persists it.</param>
    public virtual async Task<Guid?> SpawnAsync<TCandidate>(
        Guid sourceDocumentId,
        Guid? tenantId,
        string constituentKey,
        FileOrigin? fileOrigin,
        Func<Task<TCandidate?>> reloadClaimable,
        Func<TCandidate, Guid, Task> markSpawned,
        CancellationToken cancellationToken = default)
        where TCandidate : class
    {
        using (_currentTenant.Change(tenantId))
        using (var uow = _unitOfWorkManager.Begin(requiresNew: true))
        {
            var candidate = await reloadClaimable();
            if (candidate is null)
            {
                await uow.CompleteAsync();
                return null;
            }

            var derivedDocumentId = _guidGenerator.Create();
            var derived = Document.CreateDerived(
                derivedDocumentId, tenantId, fileOrigin, sourceDocumentId, constituentKey);

            // autoSave so a concurrent double-spawn trips the unique (OriginDocumentId, OriginConstituentKey) index
            // right here; the exception propagates to the caller's per-item isolation and the job is retried.
            await _documentRepository.InsertAsync(derived, autoSave: true);

            await markSpawned(candidate, derivedDocumentId);

            await _distributedEventBus.PublishAsync(
                new DocumentUploadedEto
                {
                    DocumentId = derived.Id,
                    TenantId = derived.TenantId,
                    EventTime = _clock.Now,
                    FileName = fileOrigin?.OriginalFileName,
                    FileSize = fileOrigin?.FileSize ?? 0,
                    ContentType = fileOrigin?.ContentType
                });

            // Run the derived document through the full normal pipeline. Its text-extraction job seeds Markdown from
            // the constituent (figure transcription / segment slice) instead of re-extracting it.
            await _pipelineJobScheduler.QueueAsync(derived, ExtractPipelines.Parse);

            await uow.CompleteAsync();
            return derivedDocumentId;
        }
    }
}
