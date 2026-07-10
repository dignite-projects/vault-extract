using System;
using System.Globalization;
using System.Text.Json;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Field value row</b> for type-bound fields (field architecture v2): a child entity of the <see cref="Document"/> aggregate,
/// and the <b>only</b> truth source for field value queries and persistence, replacing the old <c>Document.ExtractedFields</c> JSON column (Issue #206).
/// <para>
/// One row represents one field value. Composite primary key <c>(DocumentId, FieldDefinitionId, Order)</c> (Issue #207 + #212).
/// Internally associates the producing <see cref="FieldDefinition"/> through immutable <see cref="FieldDefinitionId"/>,
/// with no redundant field name / TypeCode strings. <see cref="FieldDefinition.Name"/> rename does not cascade to this table.
/// <see cref="Order"/> is the 0-based position of the value within a multi-value set: single-value fields always use 0
/// (unique per document and field); multi-value text fields (<c>AllowMultiple</c>) use multiple rows per field with increasing Order.
/// Whole-set rebuild / operator edits use reconcile, updating in place by <c>(FieldDefinitionId, Order)</c>, leaving no duplicate rows.
/// Values are stored in the corresponding typed column according to the write-time <c>FieldDataType</c>
/// (<see cref="TextValue"/> / <see cref="NumberValue"/> / ...). The type is determined by the referenced <see cref="FieldDefinition"/>
/// and is <b>not persisted on this row</b> (#208). This lets <c>GetFieldMatchedIdsAsync</c> use ordinary column comparisons
/// (equality + range) portably across relational databases, no longer relying on SQL Server <c>JSON_VALUE</c> / <c>TRY_CONVERT</c> dialects.
/// </para>
/// <para>
/// The <c>ExtractedFields</c> dictionary key for export DTO / MCP / REST, namely the field name, is projected by read paths
/// joining <see cref="FieldDefinition"/> with soft-delete traversal (#207). This row stores no field-name snapshot
/// (CLAUDE.md / #207 "do not introduce snapshot fields").
/// </para>
/// <para>
/// Isolation contract (CLAUDE.md "Security covenant" + Issue #206 review guardrail):
/// <list type="bullet">
///   <item>Implements <see cref="IMultiTenant"/> so ABP automatically appends tenant global filters when the child <c>DbSet</c> / navigation is used,
///   and <c>TenantId</c>-prefixed indexes are hit consistently. Queries still start from the <see cref="Document"/> aggregate root;
///   Document remains the authoritative tenant boundary.</item>
///   <item>Does <b>not</b> implement <c>ISoftDelete</c>, avoiding synchronization burden for cross-entity cascading soft delete.
///   When Document is soft-deleted, its field rows are hidden by the parent filter through the aggregate root; when Document is hard-deleted,
///   field rows are cascade-deleted.</item>
/// </list>
/// </para>
/// </summary>
public class DocumentExtractedField : Entity, IMultiTenant, IFieldValueColumns
{
    public virtual Guid? TenantId { get; private set; }

    public virtual Guid DocumentId { get; private set; }

    /// <summary><see cref="FieldDefinition"/>.Id that produced this field value (internal association / query index key, #207).</summary>
    public virtual Guid FieldDefinitionId { get; private set; }

    /// <summary>
    /// 0-based position of the value within the owning field's multi-value set (#212), participating in the composite primary key.
    /// Single-value fields always use 0; multi-value text fields (<see cref="FieldDefinition.AllowMultiple"/>) use 0,1,2...
    /// in JSON array element order.
    /// </summary>
    public virtual int Order { get; private set; }

    // Typed value columns: use one according to field type and keep the others null. Type is determined by FieldDefinition
    // and is not persisted on this row (#208). Ordinary columns can have B-tree indexes and support equality + range.
    // Number, covering both integers and decimals, goes to NumberValue.
    public virtual string? TextValue { get; private set; }
    public virtual bool? BooleanValue { get; private set; }
    public virtual decimal? NumberValue { get; private set; }
    public virtual DateOnly? DateValue { get; private set; }
    public virtual DateTime? DateTimeValue { get; private set; }

    // LongText uses a separate nvarchar(max) column for long content payloads such as summaries / descriptions.
    // It participates in no index, cannot be used as a query condition, and does not support multi-value.
    // It is intentionally separate from TextValue, which is length-limited to 256, participates in composite indexes, and supports equality queries.
    // Long text cannot be an index key; forcing it into TextValue would break the #209 index seek.
    public virtual string? LongTextValue { get; private set; }

    protected DocumentExtractedField()
    {
    }

    internal DocumentExtractedField(Guid documentId, Guid? tenantId, DocumentFieldValue value)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        FieldDefinitionId = value.FieldDefinitionId;
        Order = value.Order;
        SetValue(value);
    }

    /// <summary>
    /// Writes / updates the value in place, called for the same field during reconcile. Splits canonical JSON, already validated by
    /// App-layer <c>ExtractedFieldValueValidator</c> to align with <paramref name="value"/>.DataType, into the matching typed column.
    /// Clears all value columns before filling by type, ensuring type changes, such as the same field changing DataType under a new document type,
    /// leave no stale old-column value.
    /// </summary>
    internal void SetValue(DocumentFieldValue value)
    {
        TextValue = null;
        BooleanValue = null;
        NumberValue = null;
        DateValue = null;
        DateTimeValue = null;
        LongTextValue = null;

        var element = value.Value;
        switch (value.DataType)
        {
            case FieldDataType.Text:
                TextValue = element.GetString();
                break;
            case FieldDataType.LongText:
                LongTextValue = element.GetString();
                break;
            case FieldDataType.Number:
                NumberValue = element.GetDecimal();
                break;
            case FieldDataType.Boolean:
                BooleanValue = element.GetBoolean();
                break;
            case FieldDataType.Date:
                DateValue = DateOnly.ParseExact(element.GetString()!, FieldValueFormats.Date, CultureInfo.InvariantCulture);
                break;
            case FieldDataType.DateTime:
                DateTimeValue = DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.DataType, "Unsupported field data type.");
        }
    }

    /// <summary>
    /// Reconstructs canonical <see cref="JsonElement"/> from typed columns for on-demand assembly of the <c>ExtractedFields</c>
    /// dictionary in DTO / MCP / REST exports, preserving wire-format compatibility with the old JSON column.
    /// This is the inverse of <see cref="SetValue"/> and round-trips consistently.
    /// <paramref name="dataType"/> is supplied by the caller from the <see cref="FieldDefinition"/> referenced by this row
    /// because type is not persisted on this row (#208).
    /// <para>
    /// #501 item 8: the switch itself lives in <see cref="FieldValueFormatter"/>, shared with the export's
    /// non-entity projection, which reads the same columns and cannot call a method on this entity. Loud-fail on
    /// an unknown type is preserved there, symmetric with <see cref="SetValue"/>'s default branch.
    /// </para>
    /// </summary>
    public JsonElement ToJsonElement(FieldDataType dataType) => FieldValueFormatter.ToJsonElement(this, dataType);

    public override object[] GetKeys() => new object[] { DocumentId, FieldDefinitionId, Order };
}
