using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Issue #115 L1：业务模块向核心注册"文档持有的业务标识符"的契约。
///
/// <para>
/// 调用方：业务模块的字段抽取器（合同模块抽完合同编号 / 票据模块抽完 PO 号 / 等）
/// 在抽取得到结构化字段后，<strong>顺路</strong>调用 <see cref="RegisterAsync"/> 把该标识符与文档 id 写入索引。
/// 几乎零额外成本——业务模块本来就在做字段抽取。
/// </para>
///
/// <para>
/// 消费方：L2 关系发现 Pipeline 通过 <see cref="FindDocumentsAsync"/> 反查"持有同一标识符的其他文档"，
/// 据此自动创建 <c>RelationSource.AiSuggested</c> 的 <c>DocumentRelation</c>。
/// </para>
///
/// <para>
/// 多租户：所有方法均按 ambient <c>CurrentTenant</c> 隐式过滤；
/// 调用方不需要、也不应该传入 <c>tenantId</c>。实现侧用显式谓词加固
/// （不依赖 ABP ambient <c>DataFilter</c>，参见 <c>doc-chat-anti-patterns.md</c> 反例 C #2）。
/// </para>
///
/// <para>
/// 此契约位于 <c>Abstractions</c> 层，不依赖任何核心 Application 实现，
/// 业务模块（modules/）通过 NuGet 引用 <c>Dignite.Paperbase.Abstractions</c> 即可调用。
/// </para>
/// </summary>
public interface IDocumentIdentifierIndex
{
    /// <summary>
    /// 注册一条标识符。<strong>幂等</strong>：同一 (documentId, identifierType, identifierValue) 重复调用不会插入重复行也不抛异常。
    ///
    /// <para>
    /// <strong>幂等键不含 TenantId</strong>：DocumentId 是全局唯一 GUID 已隐含租户归属，唯一索引设计上也不含
    /// TenantId（避免单租户场景下 SQL Server NULL-distinct 语义 + EF Core <c>[TenantId] IS NOT NULL</c>
    /// filter 双重作用让唯一约束失效）。详见 <c>PaperbaseDbContextModelCreatingExtensions</c> 中 DocumentIdentifier 索引注释。
    /// </para>
    /// </summary>
    /// <param name="documentId">持有该标识符的文档 ID。</param>
    /// <param name="identifierType">标识符类型字符串（如 "ContractNumber"），由业务模块约定常量。</param>
    /// <param name="identifierValue">标识符值（如 "HT-2024-001"），自动 trim 后存储。</param>
    Task RegisterAsync(
        Guid documentId,
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 反查当前租户内所有持有 (identifierType, identifierValue) 的文档 ID（包括传入的 documentId 自身，调用方按需排除）。
    /// L2 Pipeline 的核心查询。
    /// </summary>
    Task<List<Guid>> FindDocumentsAsync(
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default);

    /// <summary>批量删除某文档名下所有标识符（文档硬删 / 重新提取 / 重新分类前清理）。</summary>
    Task RemoveByDocumentIdAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);
}
