using System;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 文件柜——人工组织归属维度（#194），与 <see cref="DocumentTypes.DocumentType"/> 正交：前者答「属于哪个组 / 批次」
/// （人工指定），后者答「这是什么」（AI 分类）。Guid 主键 + <see cref="Name"/> 层内唯一（<c>(TenantId, Name)</c>），
/// <c>Document.CabinetId</c> 以可空 Guid 引用。
/// <para>
/// 分类 / 字段抽取 pipeline 完全不读写 <c>CabinetId</c>。唯一例外是上传留空时的「AI 兜底选柜」（#265）：把本层柜的
/// <see cref="Name"/> + <see cref="Description"/>（#273）经 <c>PromptBoundary.WrapField</c> 包裹喂 LLM 选一个——
/// 独立一次性的上传时步骤，不使柜退化为第二个 DocumentType（详见 <c>CabinetSuggestionWorkflow</c>）。
/// </para>
/// </summary>
public class Cabinet : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>柜名（运行时直接展示）。唯一约束 <c>(TenantId, Name)</c>，层内不可重名。</summary>
    public virtual string Name { get; private set; } = default!;

    /// <summary>
    /// 可选柜说明（#273）。与 <see cref="Name"/> 同列喂入 #265 选柜 prompt 辅助 AI 判归属；<c>null</c> = 无说明。
    /// 同样经 <c>PromptBoundary.WrapField</c> 包裹进 LLM，故 <see cref="ValidateDescription"/> 拒控制字符做注入深度防御
    /// （镜像 <c>DocumentType.Description</c>）。
    /// </summary>
    public virtual string? Description { get; private set; }

    protected Cabinet() { }

    public Cabinet(Guid id, Guid? tenantId, string name, string? description = null)
        : base(id)
    {
        TenantId = tenantId;
        Name = ValidateName(name);
        Description = ValidateDescription(description);
    }

    public void Update(string name, string? description = null)
    {
        Name = ValidateName(name);
        Description = ValidateDescription(description);
    }

    /// <summary>Name 卫生校验：拒控制字符。Name 经 #265 选柜 prompt 进 LLM（WrapField 包裹），此处拒控制字符兼作注入深度防御。</summary>
    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), CabinetConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.Cabinet.InvalidName)
                .WithData("name", name);
        }

        return name;
    }

    /// <summary>
    /// Description 可空（null / 空白 → 归一化为 <c>null</c>，选柜 prompt 不追加该行）。有值时：长度上限 + 拒控制字符
    /// ——与 <see cref="ValidateName"/> 同源、镜像 <c>DocumentType.ValidateDescription</c> 的注入深度防御。
    /// </summary>
    private static string? ValidateDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        Check.Length(description, nameof(description), CabinetConsts.MaxDescriptionLength);

        if (description.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.Cabinet.InvalidDescription)
                .WithData("description", description);
        }

        return description;
    }
}
