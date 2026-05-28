using System;
using System.Globalization;
using System.Text.Json;
using Dignite.Paperbase.Documents.Fields;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 类型绑定字段的<b>字段值行</b>（字段架构 v2）——<see cref="Document"/> 聚合的 child entity，
/// 字段值查询与持久化的<b>唯一</b> truth source（替代旧的 <c>Document.ExtractedFields</c> JSON 列，Issue #206）。
/// <para>
/// 一行一个字段值。复合主键 <c>(DocumentId, FieldDefinitionId)</c> 即字段集自然键（Issue #207）——
/// 内部用不可变 <see cref="FieldDefinitionId"/> 关联产生该值的 <see cref="FieldDefinition"/>，
/// 不再冗余字段名 / TypeCode 字符串；<see cref="FieldDefinition.Name"/> rename 不级联本表。同文档同字段唯一，
/// 整组重建 / 操作员手改走 reconcile（同字段原地更新），不留重复行。值按写入时的 <c>FieldDataType</c> 落到对应类型化列
/// （<see cref="StringValue"/> / <see cref="DecimalValue"/> / …）——类型由所引用的 <see cref="FieldDefinition"/> 决定、<b>不在本行持久化</b>（#208），
/// 让 <c>GetFieldMatchedIdsAsync</c> 用普通列比较（等值 + 范围）跨任意关系型数据库可移植——不再依赖 SQL Server <c>JSON_VALUE</c> / <c>TRY_CONVERT</c> 方言。
/// </para>
/// <para>
/// 出口 DTO / MCP / REST 的 <c>ExtractedFields</c> 字典 key（即字段名）由读路径 join <see cref="FieldDefinition"/>
/// 投影获取（穿透 soft-delete，#207）——本行不存字段名快照（CLAUDE.md / #207 "不引入 snapshot 字段"）。
/// </para>
/// <para>
/// 隔离约定（CLAUDE.md "## 安全约定" + Issue #206 复核护栏）：
/// <list type="bullet">
///   <item>实现 <see cref="IMultiTenant"/>——child <c>DbSet</c> / navigation 被使用时 ABP 自动追加租户全局过滤，
///   且 <c>TenantId</c> 前缀索引稳定命中；但查询仍从 <see cref="Document"/> 聚合根起手，租户边界权威来源是 Document。</item>
///   <item><b>不</b>实现 <c>ISoftDelete</c>——避免跨实体级联软删的同步负担。软删 Document 时其字段行随聚合根
///   被父过滤器挡在查询之外；硬删 Document 时级联删除字段行。</item>
/// </list>
/// </para>
/// </summary>
public class DocumentExtractedField : Entity, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual Guid DocumentId { get; private set; }

    /// <summary>产生该字段值的 <see cref="FieldDefinition"/>.Id（内部关联 / 查询索引键，#207）。</summary>
    public virtual Guid FieldDefinitionId { get; private set; }

    // 类型化值列——按字段类型取用其一，其余为 null（类型由 FieldDefinition 决定、不在本行持久化，#208）。
    // 普通列即可建 B-tree 索引、支持等值 + 范围。Number（整数与小数统一）落 DecimalValue。
    public virtual string? StringValue { get; private set; }
    public virtual bool? BooleanValue { get; private set; }
    public virtual decimal? DecimalValue { get; private set; }
    public virtual DateOnly? DateValue { get; private set; }
    public virtual DateTime? DateTimeValue { get; private set; }

    protected DocumentExtractedField()
    {
    }

    internal DocumentExtractedField(Guid documentId, Guid? tenantId, DocumentFieldValue value)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        FieldDefinitionId = value.FieldDefinitionId;
        SetValue(value);
    }

    /// <summary>
    /// 原地写入 / 更新值（reconcile 时对同字段调用）。把规范 JSON（已由 App 层 <c>ExtractedFieldValueValidator</c>
    /// 校验与 <paramref name="value"/>.DataType 对齐）拆进对应类型化列；先清空所有值列再按类型回填，
    /// 保证类型切换（如同字段在新文档类型下换了 DataType）不残留旧列值。
    /// </summary>
    internal void SetValue(DocumentFieldValue value)
    {
        StringValue = null;
        BooleanValue = null;
        DecimalValue = null;
        DateValue = null;
        DateTimeValue = null;

        var element = value.Value;
        switch (value.DataType)
        {
            case FieldDataType.String:
                StringValue = element.GetString();
                break;
            case FieldDataType.Number:
                DecimalValue = element.GetDecimal();
                break;
            case FieldDataType.Boolean:
                BooleanValue = element.GetBoolean();
                break;
            case FieldDataType.Date:
                DateValue = DateOnly.ParseExact(element.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case FieldDataType.DateTime:
                DateTimeValue = DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.DataType, "Unsupported field data type.");
        }
    }

    /// <summary>
    /// 从类型化列重建规范 <see cref="JsonElement"/>——DTO / MCP / REST 出口的 <c>ExtractedFields</c> 字典即时组装时使用
    /// （wire-format 与旧 JSON 列保持兼容）。是 <see cref="SetValue"/> 的逆向，往返一致。
    /// <paramref name="dataType"/> 由调用方从该行所引用的 <see cref="FieldDefinition"/> 提供（类型不在本行持久化，#208）。
    /// </summary>
    public JsonElement ToJsonElement(FieldDataType dataType) => dataType switch
    {
        FieldDataType.String => JsonSerializer.SerializeToElement(StringValue),
        FieldDataType.Number => JsonSerializer.SerializeToElement(DecimalValue),
        FieldDataType.Boolean => JsonSerializer.SerializeToElement(BooleanValue),
        FieldDataType.Date => JsonSerializer.SerializeToElement(DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        FieldDataType.DateTime => JsonSerializer.SerializeToElement(DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        // 与 SetValue 的 default 分支对称：未知类型 loud fail，绝不吐 Undefined 毒值（否则组装进 DTO 后
        // 序列化响应会抛 "Cannot write a JsonElement with ValueKind Undefined"，整篇文档读取 500）。
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };

    public override object[] GetKeys() => new object[] { DocumentId, FieldDefinitionId };
}
