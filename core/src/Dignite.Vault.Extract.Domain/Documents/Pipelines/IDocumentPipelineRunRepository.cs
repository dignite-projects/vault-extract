using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Custom repository for <see cref="DocumentPipelineRun"/>. Split in #216 from a Document child entity into an independent aggregate root.
/// Generic CRUD is provided by inherited <see cref="IRepository{TEntity, TKey}"/>; this interface declares only custom queries required by
/// <see cref="DocumentPipelineRunManager"/> / <see cref="Document"/> orchestration paths.
/// </summary>
public interface IDocumentPipelineRunRepository : IRepository<DocumentPipelineRun, Guid>
{
    /// <summary>
    /// Gets the run with the largest <see cref="DocumentPipelineRun.AttemptNumber"/> under
    /// (<paramref name="documentId"/>, <paramref name="pipelineCode"/>); returns <c>null</c> when none exists.
    /// Used by <see cref="DocumentPipelineRunManager.QueueAsync"/> to compute next AttemptNumber;
    /// by <c>DocumentAppService.RetryPipelineAsync</c> to determine retryability; and by <c>DocumentPipelineRunAccessor.BeginOrStartAsync</c>
    /// to find the latest Pending fallback.
    /// </summary>
    Task<DocumentPipelineRun?> FindLatestByDocumentAndCodeAsync(
        Guid documentId,
        string pipelineCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the latest run for each PipelineCode in <paramref name="pipelineCodes"/> under <paramref name="documentId"/> in one call.
    /// Dictionary key = PipelineCode, returning only codes with data.
    /// Used by <see cref="DocumentPipelineRunManager.DeriveLifecycleAsync"/> to compute <see cref="Document.LifecycleStatus"/>,
    /// avoiding N round-trips.
    /// <para>
    /// <b>Contract semantics</b>: results must reflect unflushed changes in the current UoW because DeriveLifecycle immediately follows
    /// Manager calls such as <c>UpdateAsync(run, autoSave:false)</c> / Insert. The EF Core implementation merges change-tracker Local entries;
    /// the in-memory fake naturally satisfies this because it holds run references directly. Implementations must not return only stale "already committed" views.
    /// </para>
    /// </summary>
    Task<Dictionary<string, DocumentPipelineRun>> GetLatestRunsByCodesAsync(
        Guid documentId,
        IReadOnlyCollection<string> pipelineCodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries all runs by <paramref name="documentId"/>, ordered by (PipelineCode, AttemptNumber).
    /// Used by independent <c>IDocumentPipelineRunAppService.GetListByDocumentAsync</c> for the frontend document detail page.
    /// </summary>
    Task<List<DocumentPipelineRun>> GetListByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a brand-new pipeline attempt and persists it immediately (autoSave).
    /// If it collides with the <c>(DocumentId, PipelineCode, AttemptNumber)</c> unique index, whose only realistic cause is concurrent retry
    /// of the same Failed pipeline (see <see cref="DocumentPipelineRunManager.QueueAsync"/>), throws
    /// <c>BusinessException(ExtractErrorCodes.Pipeline.RetryInProgress)</c>. At that moment, the winner's new run is Pending,
    /// precisely matching the "attempt already in progress" semantics.
    /// <para>
    /// <b>Cross-DB discipline (#239)</b>: unique-constraint collision detection is centralized in the persistence layer by catching
    /// EF Core's <b>provider-agnostic</b> <c>DbUpdateException</c> type, which all providers use to wrap unique-constraint collisions.
    /// Do <b>not</b> sniff exception messages / SQL Server error codes. The Domain layer therefore references no EF Core / SqlClient types
    /// and performs no string detection.
    /// </para>
    /// </summary>
    Task InsertNewAttemptAsync(DocumentPipelineRun run, CancellationToken cancellationToken = default);
}
