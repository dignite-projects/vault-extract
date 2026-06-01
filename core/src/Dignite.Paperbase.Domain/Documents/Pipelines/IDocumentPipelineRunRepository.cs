using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// <see cref="DocumentPipelineRun"/> 的自定义仓储（拆于 #216：从 Document child entity 升为独立聚合根）。
/// 通用 CRUD 由继承的 <see cref="IRepository{TEntity, TKey}"/> 提供；这里只声明 <see cref="DocumentPipelineRunManager"/>
/// / <see cref="Document"/> 编排路径所需的自定义查询。
/// </summary>
public interface IDocumentPipelineRunRepository : IRepository<DocumentPipelineRun, Guid>
{
    /// <summary>
    /// 取 (<paramref name="documentId"/>, <paramref name="pipelineCode"/>) 下 <see cref="DocumentPipelineRun.AttemptNumber"/>
    /// 最大的 run；找不到返回 <c>null</c>。
    /// 用于：<see cref="DocumentPipelineRunManager.QueueAsync"/> 计算下一个 AttemptNumber；
    /// <c>DocumentAppService.RetryPipelineAsync</c> 判可重试；<c>DocumentPipelineRunAccessor.BeginOrStartAsync</c>
    /// 找最新 Pending fallback。
    /// </summary>
    Task<DocumentPipelineRun?> FindLatestByDocumentAndCodeAsync(
        Guid documentId,
        string pipelineCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次性查 <paramref name="documentId"/> 下、<paramref name="pipelineCodes"/> 中每个 PipelineCode 的最新 run，
    /// 字典 key = PipelineCode（仅返回有数据的 code）。
    /// 用于：<see cref="DocumentPipelineRunManager.DeriveLifecycleAsync"/> 算 <see cref="Document.LifecycleStatus"/>
    /// （避免 N 次 round-trip）。
    /// <para>
    /// <b>契约语义</b>：结果必须反映本 UoW 内尚未 flush 的修改（DeriveLifecycle 紧跟 Manager 的
    /// <c>UpdateAsync(run, autoSave:false)</c> / Insert 调用）。EFCore 实现合并 change-tracker 的 Local entries；
    /// in-memory fake 因直接持有 run 引用天然满足。实现方不得只返回"已落库"的陈旧视图。
    /// </para>
    /// </summary>
    Task<Dictionary<string, DocumentPipelineRun>> GetLatestRunsByCodesAsync(
        Guid documentId,
        IReadOnlyCollection<string> pipelineCodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 <paramref name="documentId"/> 查所有 run（按 (PipelineCode, AttemptNumber) 排序）。
    /// 用于：独立 <c>IDocumentPipelineRunAppService.GetListByDocumentAsync</c> 暴露给前端文档详情页。
    /// </summary>
    Task<List<DocumentPipelineRun>> GetListByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把实体从当前 UoW 的 EF Core change tracker 中移除（不删 DB 行）。
    /// 用于：<see cref="DocumentPipelineRunManager.QueueAsync"/> 的 AttemptNumber 唯一索引撞键 retry——
    /// 失败的 InsertAsync 把实体留在 tracker 的 Added 状态，retry 前必须先 detach 让 SaveChanges 不再重试它。
    /// 持久化层 no-op 实现可（in-memory fake）：无 tracker 概念则方法本身就无意义。
    /// </summary>
    Task DetachAsync(DocumentPipelineRun entity, CancellationToken cancellationToken = default);
}
