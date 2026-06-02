namespace Dignite.Paperbase.Documents;

public enum DocumentReviewStatus
{
    /// <summary>无需人工介入（分类置信度达标，或尚未触发分类）</summary>
    None = 0,

    /// <summary>分类置信度不足或未能产出有效类型，等待人工确认</summary>
    PendingReview = 10,

    /// <summary>已由人工确认文档类型</summary>
    Reviewed = 20,

    /// <summary>操作员拒绝审核（#237）。可恢复信号：后续 Reclassify 会把状态转回 <see cref="Reviewed"/>。</summary>
    Rejected = 30
}
