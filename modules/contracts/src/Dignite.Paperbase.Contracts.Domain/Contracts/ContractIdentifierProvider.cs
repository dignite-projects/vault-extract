using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Contracts.Contracts;

/// <summary>
/// Issue #115 L2: 合同模块对核心 <see cref="IDocumentIdentifierProvider"/> 契约的实现。
///
/// <para>
/// <strong>映射关系</strong>：
/// <list type="bullet">
/// <item><see cref="DocumentIdentifierTypes.ContractNumber"/> ↔ <see cref="Contract.ContractNumber"/></item>
/// <item><see cref="DocumentIdentifierTypes.PartyName"/> ↔ <see cref="Contract.PartyAName"/> / <see cref="Contract.PartyBName"/> / <see cref="Contract.CounterpartyName"/>
///       （任意一个字段命中即视为持有该 PartyName）</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>无独立索引</strong>：本 provider 直接查 <c>Contract</c> 聚合根字段，是当前合同数据的唯一来源——
/// 用户通过合同详情页修正 AI 抽取错误时，本 provider 自动反映新值，无同步腐烂问题。
/// 这是 L2 设计选择 fan-out provider 而非中心化索引表的核心原因（见 <c>doc-chat-anti-patterns.md</c>
/// 反例 D 同源思想：模块自治优于核心代理）。
/// </para>
///
/// <para>
/// <strong>多租户</strong>：仓储查询走 ABP <c>IMultiTenant</c> ambient filter，自动按 <c>CurrentTenant.Id</c> 过滤。
/// 本路径不接 LLM、不在 Chat 工具体内被调用，无 prompt-injection 攻击面，不需要显式权限断言三件套。
/// </para>
/// </summary>
public class ContractIdentifierProvider : IDocumentIdentifierProvider, ITransientDependency
{
    /// <summary>合同模块支持的标识符类型。L2 fan-out 时按此集合筛选。</summary>
    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        DocumentIdentifierTypes.ContractNumber,
        DocumentIdentifierTypes.PartyName,
    };

    private readonly IContractRepository _contractRepository;

    public ContractIdentifierProvider(IContractRepository contractRepository)
    {
        _contractRepository = contractRepository;
    }

    public virtual async Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var contract = await _contractRepository.FindByDocumentIdAsync(documentId);
        if (contract == null)
        {
            // 该文档不属于合同模块（或合同记录尚未创建）。返回空表示"无贡献"，
            // L2 RelationDiscoveryService 会继续遍历其他 provider。
            return Array.Empty<DocumentIdentifierEntry>();
        }

        var entries = new List<DocumentIdentifierEntry>();
        AddIfPresent(entries, DocumentIdentifierTypes.ContractNumber, contract.ContractNumber);
        AddIfPresent(entries, DocumentIdentifierTypes.PartyName, contract.PartyAName);
        AddIfPresent(entries, DocumentIdentifierTypes.PartyName, contract.PartyBName);
        AddIfPresent(entries, DocumentIdentifierTypes.PartyName, contract.CounterpartyName);
        return entries;
    }

    public virtual async Task<IReadOnlyList<Guid>> FindDocumentsAsync(
        string identifierType,
        string identifierValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identifierValue))
        {
            return Array.Empty<Guid>();
        }

        var trimmed = identifierValue.Trim();

        return identifierType switch
        {
            DocumentIdentifierTypes.ContractNumber => await FindByContractNumberAsync(trimmed, cancellationToken),
            DocumentIdentifierTypes.PartyName => await FindByPartyNameAsync(trimmed, cancellationToken),
            _ => Array.Empty<Guid>()      // Unknown type — defensive; SupportedIdentifierTypes 已经在上游筛过
        };
    }

    protected virtual async Task<IReadOnlyList<Guid>> FindByContractNumberAsync(
        string contractNumber,
        CancellationToken ct)
    {
        var contracts = await _contractRepository.FindByContractNumberAsync(contractNumber, ct);
        return contracts.Select(c => c.DocumentId).Where(id => id != Guid.Empty).Distinct().ToList();
    }

    protected virtual async Task<IReadOnlyList<Guid>> FindByPartyNameAsync(
        string partyName,
        CancellationToken ct)
    {
        var contracts = await _contractRepository.GetListByPartyNameAsync(partyName, ct);
        return contracts.Select(c => c.DocumentId).Where(id => id != Guid.Empty).Distinct().ToList();
    }

    private static void AddIfPresent(List<DocumentIdentifierEntry> entries, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        entries.Add(new DocumentIdentifierEntry(type, value.Trim()));
    }
}
