using System;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文件柜实体——人工组织归属维度（#194）。两层独立单层模型（参照 <see cref="DocumentType"/>）：
/// <list type="bullet">
///   <item><c>TenantId == null</c> → Host 部署级柜（Host admin 通过 <c>ICabinetAppService</c> 自助 CRUD）</item>
///   <item><c>TenantId != null</c> → 租户私有柜（租户 admin 自助 CRUD）</item>
/// </list>
/// 与 <see cref="DocumentType"/> 正交：DocumentType 答"这是什么"（AI 分类），Cabinet 答"属于哪个组 / 批次"（人工指定）。
/// 一个文档可同时"在法务部柜里" + "类型是合同"。
/// <para>
/// 与 DocumentType 的关键区别——<b>无字符串标识码</b>：DocumentType 的 TypeCode 之所以是字符串，是因为要喂
/// LLM 分类 prompt、被下游业务按 <c>(TenantId, TypeCode)</c> 元组路由、允许跨层同码。Cabinet 三者皆无
/// （#194 约束：正交于 pipeline，不进 LLM / 出口契约，只用于内部查询 / 筛选 / 分组），故用 Guid 主键
/// + <see cref="DisplayName"/> 层内唯一即可，<see cref="Document.CabinetId"/> 以可空 Guid 外键引用。
/// </para>
/// </summary>
public class Cabinet : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>柜名（运行时直接展示）。唯一约束 <c>(TenantId, DisplayName)</c>，层内不可重名。</summary>
    public virtual string DisplayName { get; private set; } = default!;

    protected Cabinet() { }

    public Cabinet(Guid id, Guid? tenantId, string displayName)
        : base(id)
    {
        TenantId = tenantId;
        DisplayName = ValidateDisplayName(displayName);
    }

    public void Update(string displayName)
    {
        DisplayName = ValidateDisplayName(displayName);
    }

    /// <summary>
    /// DisplayName 卫生校验：拒绝控制字符（换行 / 制表符等）。
    /// 不同于 <see cref="DocumentType"/> 同名校验的 prompt injection 边界目的（DocumentType 进 LLM）——
    /// Cabinet 正交于 pipeline 不进 LLM，此处纯为防 UI / CSV 注入的基础卫生。
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), CabinetConsts.MaxDisplayNameLength);

        if (displayName.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidCabinetDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }
}
