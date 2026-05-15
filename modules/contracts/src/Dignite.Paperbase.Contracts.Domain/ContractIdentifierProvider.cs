using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Contracts;

/// <summary>
/// Issue #115 L2: 合同模块对核心 <see cref="IDocumentIdentifierProvider"/> 契约的实现。
///
/// <para>
/// <strong>映射关系</strong>：
/// <list type="bullet">
/// <item><see cref="DocumentIdentifierTypes.ContractNumber"/> ↔ <see cref="Contract.ContractNumber"/></item>
/// <item><see cref="DocumentIdentifierTypes.PartyName"/> ↔ <see cref="Contract.PartyAName"/> / <see cref="Contract.PartyBName"/>
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
    /// <summary>
    /// 合同模块支持的标识符类型。仅 <see cref="DocumentIdentifierTypes.ContractNumber"/>——
    /// PartyName 故意不暴露（codex review fix [high]：作为 L2 强标识符会形成
    /// 高置信度假关系图，留给 L3 LLM 评判）。
    /// </summary>
    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        DocumentIdentifierTypes.ContractNumber,
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
        // PartyName intentionally NOT emitted (codex review fix [high]).
        // 一家供应商可能有上百份合同；以 PartyName 为 L2 强标识符会形成高置信度假关系图，
        // 污染 chat 路径的 cross-document 推理。Party 关系判断留给 L3 LLM。
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

        // 硬伤一 (Phase 1): defensively normalize the lookup value. L2 RelationDiscoveryService
        // already normalizes before calling, but idempotent re-normalization here lets callers
        // outside the L2 fan-out path (future code, tests passing raw values) still match
        // correctly. NormalizeIdentifierCode is idempotent — normalized values pass through unchanged.
        var normalized = DocumentIdentifierNormalization.Normalize(identifierType, identifierValue);
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<Guid>();
        }

        // PartyName intentionally absent — defensive return-empty for any unsupported type
        // (codex review fix [high]).
        return identifierType switch
        {
            DocumentIdentifierTypes.ContractNumber => await FindByContractNumberAsync(normalized, cancellationToken),
            _ => Array.Empty<Guid>()
        };
    }

    /// <summary>
    /// Accepts an ALREADY-normalized contract number (see <see cref="FindDocumentsAsync"/>
    /// and <see cref="Contract.NormalizedContractNumber"/>). Repository lookup goes through
    /// the indexed normalized column.
    /// </summary>
    protected virtual async Task<IReadOnlyList<Guid>> FindByContractNumberAsync(
        string normalizedContractNumber,
        CancellationToken ct)
    {
        var contracts = await _contractRepository.FindByContractNumberAsync(normalizedContractNumber, ct);
        return contracts.Select(c => c.DocumentId).Where(id => id != Guid.Empty).Distinct().ToList();
    }

    private static void AddIfPresent(List<DocumentIdentifierEntry> entries, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        entries.Add(new DocumentIdentifierEntry(type, value.Trim()));
    }
}
