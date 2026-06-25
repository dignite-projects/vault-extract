namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Field data type, affecting LLM extraction schema hints and downstream parsing behavior.
/// Used by the unified <c>FieldDefinition</c> entity, which distinguishes host vs tenant layers by
/// TenantId; see CLAUDE.md "Type-bound fields (Mechanism B)".
/// <para>
/// <see cref="Number"/> uniformly represents integers and decimals, stored as decimal for exact
/// integer values and a range far beyond long. Integer / Decimal are intentionally not separated:
/// their query behavior is the same (numeric equality + ranges), and merging removes the misselection
/// surface where someone first chooses Integer and later needs decimals but is blocked by DataType
/// change guards. Likewise, <see cref="Date"/> and <see cref="DateTime"/> stay separate: pure dates
/// are the most common time fields in documents, and forcing them into DateTime would invent
/// nonexistent hours / minutes / seconds and degrade date equality into ranges.
/// </para>
/// <para>
/// <see cref="Text"/> and <see cref="LongText"/> are intentionally separate because they serve two
/// uses: short structured values vs long content.
/// <list type="bullet">
///   <item><see cref="Text"/>: structured short values such as names, numbers, currencies, or case
///   reasons. Limited to 256 (<c>DocumentExtractedFieldConsts.MaxTextValueLength</c>), stored in the
///   <c>TextValue</c> column, included in the composite index key, and supports equality query +
///   multiple values (#209 / #212).</item>
///   <item><see cref="LongText"/>: long content such as summaries, descriptions, or risk notes,
///   extracted through tenant-configured fields under Mechanism B. Stored in a separate
///   <c>LongTextValue</c> column (<c>nvarchar(max)</c>), <b>not included in any index, not queryable,
///   and not multi-value</b>. It is a pure storage payload and outbound DTOs render it as a string.
///   Note that this is a user-configured <b>type-bound field</b> under Mechanism B and is orthogonal
///   to "the system does not persist full-document Summary / derived text" in CLAUDE.md; that rule
///   constrains system-generic fields, not user-configured schema.</item>
/// </list>
/// </para>
/// </summary>
public enum FieldDataType
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    DateTime = 4,
    LongText = 5
}
