using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Contracts.Contracts;

public interface IContractRepository : IRepository<Contract, Guid>
{
    Task<Contract?> FindByDocumentIdAsync(Guid documentId);

    /// <summary>
    /// Issue #115 L2: 查询当前租户内持有指定合同编号的所有合同。
    /// 同一编号理论上唯一，但保留 List 返回类型——AI 抽取出错或人工录入重复时
    /// 不希望调用方收到 InvalidOperationException 而无法上下文判断。
    /// 调用方负责按 (Tenant, ContractNumber) 唯一性的业务约定处理多结果。
    /// </summary>
    Task<List<Contract>> FindByContractNumberAsync(
        string contractNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issue #115 L2: 查询当前租户内"甲方 / 乙方 / 对手方"匹配指定名称的所有合同。
    /// 同一公司可在不同合同里出现在不同位置，所以本方法跨 PartyAName / PartyBName / CounterpartyName
    /// 三个字段查询。
    /// </summary>
    Task<List<Contract>> GetListByPartyNameAsync(
        string partyName,
        CancellationToken cancellationToken = default);
}
