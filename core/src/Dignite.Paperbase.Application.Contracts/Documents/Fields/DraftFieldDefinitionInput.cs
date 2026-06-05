using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 「按提示词起草字段元数据」输入（issue #264）。admin 把抽取指令（提示词）作为主输入，
/// 后端用 LLM 一次性**起草**字段其余元数据，admin 再逐项核对 / 修改后保存。
/// </summary>
public class DraftFieldDefinitionInput
{
    /// <summary>
    /// 抽取指令——起草的唯一输入信号。长度上限复用 <see cref="FieldDefinitionConsts.MaxPromptLength"/>
    /// （它最终也会落进 <c>FieldDefinition.Prompt</c>，同一护栏）。
    /// </summary>
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string Prompt { get; set; } = default!;

    /// <summary>
    /// true = 新建字段：草稿**额外建议**机器键 <see cref="FieldDefinitionDraftDto.Name"/>；
    /// false = 编辑既有字段：<c>Name</c> 是契约级冻结身份键（#207 / 下游契约 ID / ExtractedFields 字典 key），
    /// 草稿**不动它**——服务端恒回吐空 Name，避免静默 churn 下游契约键（issue #264 护栏 1）。
    /// </summary>
    public bool ForNewField { get; set; }
}
