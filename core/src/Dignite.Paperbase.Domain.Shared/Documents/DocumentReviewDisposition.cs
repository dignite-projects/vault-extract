namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文档人工审核的<b>处置阶段</b>（操作员动作轴）——只记录"操作员对这份文档做了什么"。
/// 与<b>待审原因</b>（<see cref="DocumentReviewReasons"/>，客观未解决问题）正交：处置轴只由操作员动作写
/// （确认 / 拒绝），原因轴只由 pipeline / evaluator 写，写入点零交叉。
/// <para>
/// "是否需要操作员关注"由 <c>ReviewReasons != None 且本枚举 != Rejected</c> 派生（见 <see cref="ReviewReasonPolicy"/>），<b>不</b>在本枚举里用一个值表达——
/// 旧的 <c>PendingReview</c> 已迁为 <see cref="DocumentReviewReasons.UnresolvedClassification"/>（它是"原因"不是"处置"）。
/// 成员数值与旧 <c>DocumentReviewStatus</c> 对齐（DB 列 int 值不变）：None→NotReviewed(0)、Reviewed→Confirmed(20)、Rejected(30)。
/// </para>
/// </summary>
public enum DocumentReviewDisposition
{
    /// <summary>操作员尚未处置（默认）。是否需要关注取决于 <see cref="DocumentReviewReasons"/>。</summary>
    NotReviewed = 0,

    /// <summary>操作员已确认文档类型（人工分类 / 重分类）。</summary>
    Confirmed = 20,

    /// <summary>
    /// 操作员拒绝（#237，可恢复：后续 Reclassify 会转回 <see cref="Confirmed"/>）。
    /// 拒绝必带 <c>Document.RejectionReason</c>。
    /// </summary>
    Rejected = 30
}
