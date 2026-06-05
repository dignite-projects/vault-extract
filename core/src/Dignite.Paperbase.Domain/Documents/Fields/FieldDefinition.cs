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

    /// <summary>
    /// LLM 抽取指令——告诉模型从文档中找什么值。<b>选填</b>：留空（null）时模型仅靠 <see cref="Name"/>（机器名）+
    /// <see cref="DataType"/> 推断该抽什么。字段名足够自解释时（如 <c>contract_amount</c> / <c>issue_date</c>）可不填。
    /// </summary>
    public virtual string? Prompt { get; private set; }

    public virtual FieldDataType DataType { get; private set; }

    public virtual int DisplayOrder { get; private set; }

    public virtual bool IsRequired { get; private set; }

    /// <summary>
    /// 是否允许多值（#212）——仅对 <see cref="FieldDataType.Text"/> 字段有效。为 true 时该字段的抽取值落成
    /// 多行 <see cref="DocumentExtractedField"/>（复合键含 <c>Order</c> 位序），出口 <c>ExtractedFields</c> 渲染为 JSON 数组；
    /// LLM 抽取 schema 告知模型返回 <c>string[]</c>。非文本类型强行开多值由实体层 loud fail（见 <see cref="ValidateMultiValue"/>）。
    /// </summary>
    public virtual bool AllowMultiple { get; private set; }

    protected FieldDefinition() { }

    public FieldDefinition(
        Guid id,
        Guid? tenantId,
        Guid documentTypeId,
        string name,
        string displayName,
        string? prompt,
        FieldDataType dataType,
        int displayOrder = 0,
        bool isRequired = false,
        bool allowMultiple = false)
        : base(id)
    {
        TenantId = tenantId;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = NormalizePrompt(prompt);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        AllowMultiple = ValidateMultiValue(allowMultiple, dataType);
    }

    /// <summary>
    /// 更新字段定义。rename <see cref="Name"/> 是契约级变更（下游 / LLM prompt schema 依赖），UI 应警示。
    /// <paramref name="dataType"/> 对已有抽取值的字段禁止改类型（防 typed-column 错位），
    /// <paramref name="allowMultiple"/> 由 multi→single 收窄对已有值字段禁止（防 Order&gt;0 行变孤儿）——两者均由 AppService 调用前断言。
    /// </summary>
    public void Update(string name, string displayName, string? prompt, FieldDataType dataType, int displayOrder, bool isRequired, bool allowMultiple)
    {
        Name = ValidateName(name);
        DisplayName = ValidateDisplayName(displayName);
        Prompt = NormalizePrompt(prompt);
        DataType = dataType;
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        AllowMultiple = ValidateMultiValue(allowMultiple, dataType);
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

    /// <summary>
    /// 把候选显示名规范化为**可安全保存**的形态：控制字符（换行 / tab / null byte 等）替为空格、折叠连续空白、
    /// 截断到 <see cref="FieldDefinitionConsts.MaxDisplayNameLength"/>。
    /// <para>
    /// 供「按提示词起草」(#264) 预填表单用——刻意与 <see cref="ValidateDisplayName"/> 同处一类、共享同一控制字符
    /// 拒绝域：起草产出经此规范化后**保证**能过 <see cref="ValidateDisplayName"/>，不会一保存就 loud-fail；
    /// 且 display-name 的清洗 policy 单点落在实体（truth source）上，避免 app 层另起一套发散 sanitizer
    /// （日后收紧拒绝域时二者不会静默漂移）。空白输入 → 空字符串（调用方据空判定「起草不可用」）。
    /// </para>
    /// </summary>
    public static string NormalizeDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length > FieldDefinitionConsts.MaxDisplayNameLength)
        {
            cleaned = cleaned[..FieldDefinitionConsts.MaxDisplayNameLength];
            // 按 UTF-16 码元截断可能切断代理对，残留孤立高代理项（ValidateDisplayName 放行、却是非法配对字符，
            // 会破坏后续 JSON 序列化 / DB 往返）——丢弃末位孤立高代理项（#264 review2 #2）。
            if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.Trim();
        }

        return cleaned;
    }

    /// <summary>
    /// 规范化选填的抽取指令：空白（null / 纯空格）一律收敛为 null（语义"无 prompt"，留空时 LLM 仅靠 Name + DataType 推断），
    /// 非空时仅校验长度上限（<see cref="FieldDefinitionConsts.MaxPromptLength"/>）。不再 NotNullOrWhiteSpace——Prompt 已是选填项。
    /// </summary>
    private static string? NormalizePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        Check.Length(prompt, nameof(prompt), FieldDefinitionConsts.MaxPromptLength);
        return prompt;
    }

    /// <summary>
    /// 多值仅对 <see cref="FieldDataType.Text"/> 有意义（#212）：多值落多行（复合键含 Order）只有文本是
    /// "短结构化值列表"语义（标签 / 关键词 / 多当事人）；Number/Boolean/Date/DateTime 的多值无现实抽取场景，
    /// 且会让类型化列查询语义含糊。非文本强行开多值 → loud fail。
    /// </summary>
    private static bool ValidateMultiValue(bool allowMultiple, FieldDataType dataType)
    {
        if (allowMultiple && dataType != FieldDataType.Text)
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinition.MultiValueRequiresStringType)
                .WithData("dataType", dataType.ToString());
        }

        return allowMultiple;
    }
}
