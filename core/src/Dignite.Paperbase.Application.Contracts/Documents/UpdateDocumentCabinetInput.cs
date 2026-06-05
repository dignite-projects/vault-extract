using System;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 改派文档所属文件柜的输入（#257）。
/// <para>
/// <see cref="CabinetId"/> 为 <c>null</c> 表示移出文件柜（未归类）——故意<b>不</b>加 <c>[Required]</c>，
/// null 是合法语义而非"未提供"。非 null 须为当前层（<see cref="Volo.Abp.MultiTenancy.ICurrentTenant"/>）
/// 已存在的柜，存在性由 <c>DocumentAppService.UpdateCabinetAsync</c> 校验。
/// </para>
/// </summary>
public class UpdateDocumentCabinetInput
{
    /// <summary>目标文件柜 Id；<c>null</c> = 移出文件柜（未归类）。</summary>
    public Guid? CabinetId { get; set; }
}
