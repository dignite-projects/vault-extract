namespace Dignite.Vault.Extract.Documents;

public static class DocumentExtractedFieldConsts
{
    /// <summary>
    /// Maximum length for type-bound <c>Text</c> field values.
    /// <para>
    /// This is both the DB column length (<c>TextValue nvarchar(256)</c>) and the application-layer
    /// validation limit (<c>ExtractedFieldValueValidator</c>). They must stay aligned: every value
    /// accepted by validation must fit in storage. Changing this value requires regenerating an EF
    /// migration.
    /// </para>
    /// <para>
    /// A bounded length, rather than <c>nvarchar(max)</c>, allows <c>TextValue</c> to be part of the
    /// <c>(TenantId, FieldDefinitionId, TextValue, DocumentId)</c> composite index key, so text-field
    /// equality queries can use a pure index seek (#209). Type-bound text fields are structured short
    /// values extracted from Markdown, such as names, numbers, currencies, or case reasons. They do
    /// not carry long text; long text belongs in <c>Document.Markdown</c>. A limit of 256 is generous,
    /// and 256 * 2 bytes + three Guids (48 bytes) = 560 bytes, safely below SQL Server's 1700-byte
    /// nonclustered index key limit.
    /// </para>
    /// </summary>
    public static int MaxTextValueLength { get; set; } = 256;

    /// <summary>
    /// Maximum length for type-bound <c>LongText</c> field values (application validation limit + LLM
    /// schema soft hint).
    /// <para>
    /// This is not sourced from <see cref="MaxTextValueLength"/>'s 256. <c>LongText</c> is stored in a
    /// separate <c>LongTextValue nvarchar(max)</c> column, is <b>not included in any index, and cannot
    /// be used as a query condition</b>; the DB column itself is unbounded. This limit is an
    /// <b>anti-abuse guardrail</b> that prevents malicious / uncontrolled documents from inducing the
    /// LLM to emit huge strings for long-text fields and bloating prompt round trips and DB rows. 8000
    /// characters comfortably covers long-content extraction cases such as summaries, descriptions,
    /// and risk notes. The real full-text payload belongs in <c>Document.Markdown</c>, not type-bound
    /// fields. Changing this value does not require rebuilding indexes because the column is
    /// unbounded, but it must stay synchronized with <c>ExtractedFieldValueValidator</c> and the
    /// schema maxLength in <c>FieldExtractionWorkflow</c>.
    /// </para>
    /// </summary>
    public static int MaxLongTextValueLength { get; set; } = 8000;

    /// <summary>
    /// Maximum number of values for one multi-value field (<c>FieldDefinition.AllowMultiple</c>,
    /// #212), which is the hard cap on expanded rows for one document and one field.
    /// <para>
    /// This is the "hard result-set cap" for the LLM-triggered write path, matching the write-side
    /// equivalent of the CLAUDE.md security covenant / <c>llm-call-anti-patterns.md</c> section 2.9.
    /// A malicious document can induce the LLM to emit a huge array for a multi-value field and
    /// inflate <c>DocumentExtractedField</c> rows element by element. The two-layer guardrail is an LLM
    /// schema <c>maxItems</c> soft constraint plus <c>ExtractedFieldValueValidator</c> hard validation:
    /// over-limit groups are invalid as a whole, the LLM path stores null, and operator edits fail
    /// loudly. This matches the project's "schema hint + validator fallback" style. Multi-values only
    /// carry short structured lists, such as tags, keywords, or multiple parties, so 100 is generous.
    /// </para>
    /// </summary>
    public static int MaxMultiValueCount { get; set; } = 100;
}
