using System;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// The typed value columns of one field-value row, read-only. Exactly one is non-null, chosen by the owning
/// <c>FieldDefinition.DataType</c> — the type is not persisted on the row (#208), so every reader must be told
/// which <see cref="FieldDataType"/> to interpret these by.
/// <para>
/// #501 item 8: implemented by the persisted <c>DocumentExtractedField</c> entity <b>and</b> by the export's
/// non-entity projection, so <see cref="FieldValueFormatter"/> can render either without a second copy of the
/// <see cref="FieldDataType"/> switch. The projection exists precisely so the export does not load the entity
/// (it would drag <c>Markdown</c> into memory and into the change tracker), which is why a method on the entity
/// could not have served both.
/// </para>
/// </summary>
public interface IFieldValueColumns
{
    string? TextValue { get; }

    string? LongTextValue { get; }

    bool? BooleanValue { get; }

    decimal? NumberValue { get; }

    DateOnly? DateValue { get; }

    DateTime? DateTimeValue { get; }
}
