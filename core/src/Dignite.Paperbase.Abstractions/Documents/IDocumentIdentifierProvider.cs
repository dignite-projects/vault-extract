using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// Issue #115 L2：业务模块向核心暴露的"标识符查询"契约。
///
/// <para>
/// <strong>设计原则：核心层不持有标识符数据，业务模块是单一来源</strong>。
/// 合同编号已经存在 <c>Contract.ContractNumber</c> 字段、PO 号已经存在 <c>Invoice.PoNumber</c> 字段——
/// 业务模块字段是这些标识符的唯一权威。L2 关系发现通过本契约在所有业务模块间 fan-out 查询，
/// 避免在核心层重复存储一份索引（这会引入数据冗余 + 用户编辑后的同步腐烂问题）。
/// </para>
///
/// <para>
/// 实现位于业务模块（如 <c>Dignite.Paperbase.Contracts.Domain.Contracts.ContractIdentifierProvider</c>）；
/// 实现类应注册为 <see cref="Volo.Abp.DependencyInjection.ITransientDependency"/>，
/// L2 通过 DI 收集 <c>IEnumerable&lt;IDocumentIdentifierProvider&gt;</c> 自动 fan-out。
/// </para>
///
/// <para>
/// 多租户：实现侧用 ABP <c>IMultiTenant</c> ambient filter（仓储默认行为）；
/// 不需要从入参收 <c>tenantId</c>。
/// </para>
///
/// <para>
/// 此契约位于 <c>Abstractions</c> 层，业务模块通过 NuGet 引用 <c>Dignite.Paperbase.Abstractions</c> 即可实现，
/// 不被迫依赖核心 Application 层。
/// </para>
/// </summary>
public interface IDocumentIdentifierProvider
{
    /// <summary>
    /// 该 provider 支持的标准化 identifier 类型集合（参见 <see cref="DocumentIdentifierTypes"/>）。
    /// L2 fan-out 时按此集合筛选——provider 不支持的类型不会被调用，避免无谓查询。
    /// </summary>
    IReadOnlyCollection<string> SupportedIdentifierTypes { get; }

    /// <summary>
    /// 返回该 provider 名下的某文档持有的所有标识符 (type, value) 对。
    /// 若该 provider 不拥有该文档（例如合同 provider 收到一份发票），返回空列表（不抛异常）。
    /// 同一类型可能有多条值（如 <c>PartyName</c> 可能同时是甲方和乙方），所以返回 <see cref="IReadOnlyList{T}"/> 而非字典。
    /// </summary>
    Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 反查：当前租户内该 provider 名下，持有指定 (type, value) 的文档 ID 列表。
    /// L2 主查询路径——找到与源文档共享标识符的对端文档。
    /// 实现侧应依赖 ABP ambient <c>DataFilter</c> 自动按 <c>CurrentTenant</c> 过滤。
    /// </summary>
    Task<IReadOnlyList<Guid>> FindDocumentsAsync(
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 标识符条目（不可变值对象）。
/// </summary>
/// <param name="Type">标识符类型，应为 <see cref="DocumentIdentifierTypes"/> 中的常量。</param>
/// <param name="Value">标识符值，已规范化（trim 后）。</param>
public sealed record DocumentIdentifierEntry(string Type, string Value);
