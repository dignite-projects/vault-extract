namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 「按提示词起草」输出（issue #264）：AI 起草的字段元数据**草稿**。
/// <para>
/// 这是**一次性草稿、非持续真值派生**——所有字段落进前端表单控件后用户仍可逐项核对 / 修改再保存。
/// <c>Name</c> 仅在 <see cref="DraftFieldDefinitionInput.ForNewField"/> 为 true 时填充（已 sanitize 为白名单 slug）；
/// 编辑既有字段时恒为空字符串（护栏 1：契约级身份键冻结，不被 AI 覆盖）。
/// </para>
/// <para>
/// 任意字段可能为**空 / 默认值**：LLM 不可用 / 超时 / 返回非 JSON 时整体回退为保守草稿
/// （空 DisplayName + 空 Name + <see cref="FieldDataType.Text"/> + 全 false），前端据空 DisplayName
/// 判定「起草不可用」、保留用户已填内容并提示手填。
/// </para>
/// </summary>
public class FieldDefinitionDraftDto
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>仅当输入 <see cref="DraftFieldDefinitionInput.ForNewField"/>=true 时填充；编辑场景恒为空。</summary>
    public string Name { get; set; } = string.Empty;

    public FieldDataType DataType { get; set; } = FieldDataType.Text;

    /// <summary>护栏 3：文档语义里没有「是否必填」信号，AI 只给保守默认 false，由 admin 自行决定。</summary>
    public bool IsRequired { get; set; }

    /// <summary>护栏 2：仅 <see cref="FieldDataType.Text"/> 字段可为 true（镜像实体不变量），非文本恒被服务端钳为 false。</summary>
    public bool AllowMultiple { get; set; }
}
