namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段数据类型——影响 LLM 抽取时的 schema 提示与下游解析行为。
/// 用于统一 <c>FieldDefinition</c> 实体（按 TenantId 区分 Host vs 租户层；详见 CLAUDE.md "类型绑定字段（B 机制）"）。
/// <para>
/// <see cref="Number"/> 统一表示整数与小数（decimal 存储，对整数精确且范围远超 long）——刻意不区分 Integer / Decimal：
/// 二者查询行为相同（数值等值 + 区间），合并消除"先选 Integer、后来要小数却被 DataType 变更守卫挡住"的错选面。
/// 同理保留 <see cref="Date"/> 与 <see cref="DateTime"/> 分开——纯日期是文档里最常见的时间字段，
/// 强并成 DateTime 会逼出不存在的时分秒、把日期等值退化成区间，得不偿失。
/// </para>
/// </summary>
public enum FieldDataType
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    DateTime = 4
}
