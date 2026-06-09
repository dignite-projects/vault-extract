using System;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文档<b>待审原因</b>集合（客观未解决问题轴）——回答"为什么这份文档需要操作员关注"。
/// 与<b>处置阶段</b>（<see cref="DocumentReviewDisposition"/>）正交：本集合只由 pipeline / evaluator 维护，
/// 每个 bit 由<b>唯一一个</b>阶段负责（按位 set / clear，互不覆盖）；<c>None</c> 表示无未解决原因。
/// <para>
/// 原因分两类（见 <see cref="ReviewReasonPolicy"/>）：<b>blocking</b>（阻断 Ready，文档对下游不可用）与
/// <b>non-blocking</b>（不阻断下游，仅进操作员队列）。未来新增原因 = 加一个成员（non-blocking 零额外改动；
/// blocking 再 OR 进 <see cref="ReviewReasonPolicy.Blocking"/>）——判定结构不变。
/// </para>
/// 以 <c>[Flags]</c> int 单列持久化（跨库可移植，#206；读路径零 JOIN）。
/// </summary>
[Flags]
public enum DocumentReviewReasons
{
    /// <summary>无未解决原因。</summary>
    None = 0,

    /// <summary>
    /// 分类未定（低置信度 / 无法分类）。<b>blocking</b>——无已确认类型，文档对下游不可用，挡住 Ready。
    /// 由分类阶段维护（取代旧 <c>DocumentReviewStatus.PendingReview</c>）。
    /// </summary>
    UnresolvedClassification = 1 << 0,

    /// <summary>
    /// 该类型声明的必填字段（<c>FieldDefinition.IsRequired</c>）未抽到值。<b>non-blocking</b>——
    /// 文档照常 Ready / 照常发 <c>DocumentReadyEto</c>，只进操作员"待补录"队列。由字段抽取阶段维护。
    /// </summary>
    MissingRequiredFields = 1 << 1
}
