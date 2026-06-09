using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 操作员主动修正文档分类的输入。
/// <para>
/// 与 <see cref="ConfirmClassificationInput"/> 的区别：Confirm 用于待人工审核（UnresolvedClassification）状态下的"确认"
/// 语义；Reclassify 是任意状态下的"操作员认为分类不对，覆写"语义。两者最终都把
/// <see cref="Document.ReviewDisposition"/> 落到 Confirmed，但 API 分离便于审计与权限治理。
/// </para>
/// </summary>
public class ReclassifyDocumentInput
{
    /// <summary>覆写分类的目标文档类型不可变 Id（#207：内部稳定句柄，TypeCode 可由 admin 重命名故不作引用键）。</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }
}
