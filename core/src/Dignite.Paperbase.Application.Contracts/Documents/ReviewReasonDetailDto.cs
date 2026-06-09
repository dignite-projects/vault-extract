using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 单条待审原因的结构化明细（#284）——供操作员 UI 渲染。<b>服务端算 / 客户端纯渲染</b>：
/// <see cref="IsBlocking"/> 由服务端按 <c>ReviewReasonPolicy</c> 填（客户端不重判哪些 blocking）；
/// <see cref="MissingFieldNames"/> 仅 <see cref="DocumentReviewReasons.MissingRequiredFields"/> 时有值（缺失必填字段的 DisplayName）。
/// </summary>
public class ReviewReasonDetailDto
{
    /// <summary>该明细对应的单个原因（非组合）。</summary>
    public DocumentReviewReasons Reason { get; set; }

    /// <summary>是否阻断下游（Ready 闸门）。服务端按 policy 投影，客户端直接用。</summary>
    public bool IsBlocking { get; set; }

    /// <summary>缺失的必填字段显示名（仅 MissingRequiredFields 原因时非空）。</summary>
    public List<string>? MissingFieldNames { get; set; }
}
