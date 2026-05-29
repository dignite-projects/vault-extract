using System;
using System.Linq;
using System.Text.RegularExpressions;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段定义实体。唯一约束 <c>(TenantId, DocumentTypeId, Name)</c>；字段抽取严格匹配单层、不跨层 union。
/// </summary>
public class FieldDefinition : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly Regex NameRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    /// <summary>父文档类型内部关联——引用 <see cref="DocumentType"/>.Id（reference-by-id，无 navigation；#207）。</summary>
    public virtual Guid DocumentTypeId { get; private set; }

    /// <summary>
    /// 机器契约 key——作为 LLM prompt 的 JSON schema key、<c>ExtractedFields</c> 字典 key、下游契约 ID；
    /// #207 起可由 admin 重命名（内部关联走不可变 Id，rename 不级联）。受
    /// <see cref="FieldDefinitionConsts.NamePattern"/> 白名单约束——它裸拼进 prompt schema，白名单即注入防线。
    /// </summary>
    public virtual string Name { get; private set; } = default!;

    /// <summary>显示名称（人类可读，运行时直接展示）。<b>不进 LLM prompt</b>。</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>LLM 抽取指令——告诉模型从文档中找什么值。</summary>
    public virtual string Prompt { get; private set; } = default!;

    public virtual FieldDataType DataType { get; private set; }

    public virtual int DisplayOrder { get; private set; }

    public virtual bool IsRequired { get; private set; }

    protected FieldDefinition() { }

    public FieldDefinition(
        Guid id,
        Guid? tenantId,
        Guid documentTypeId,
        string name,
        string displayName,
        string prompt,
        FieldDataType dataType,
        int displayOrder = 0,
        bool isRequired = false)
        : base(id)
    {
        TenantId = tenantId;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = Check.NotNullOrWhiteSpace(prompt, nameof(prompt), FieldDefinitionConsts.MaxPromptLength);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
    }

    /// <summary>
    /// 更新字段定义。rename <see cref="Name"/> 是契约级变更（下游 / LLM prompt schema 依赖），UI 应警示。
    /// <paramref name="dataType"/> 对已有抽取值的字段禁止改类型（防 typed-column 错位）由 AppService 调用前断言。
    /// </summary>
    public void Update(string name, string displayName, string prompt, FieldDataType dataType, int displayOrder, bool isRequired)
    {
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = Check.NotNullOrWhiteSpace(prompt, nameof(prompt), FieldDefinitionConsts.MaxPromptLength);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
    }

    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), FieldDefinitionConsts.MaxNameLength);
        if (!NameRegex.IsMatch(name))
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.InvalidName)
                .WithData("name", name)
                .WithData("pattern", FieldDefinitionConsts.NamePattern);
        }
        return name;
    }

    /// <summary>
    /// DisplayName 不进 LLM prompt；此处拒绝控制字符是深度防御 hygiene——防 UI 渲染 / 日志承受换行 / null byte 等。
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), FieldDefinitionConsts.MaxDisplayNameLength);

        if (displayName.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.InvalidDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }
}
