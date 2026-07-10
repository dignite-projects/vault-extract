using System;
using System.Globalization;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// The one place typed field-value columns are turned back into something a reader can consume: the canonical
/// JSON scalar of the egress contract, or the display string of an exported cell.
/// <para>
/// #501 item 8: these two switches, plus the inverse in <c>DocumentExtractedField.SetValue</c>, used to be three
/// independent <see cref="FieldDataType"/> switches carrying their own copies of the date format literals — with
/// a fourth copy in <c>FieldFingerprintCalculator</c>. Adding a <see cref="FieldDataType"/> member meant editing
/// each, and forgetting one was silent. The literals now live in <see cref="FieldValueFormats"/> and every
/// reader routes through here; <c>FieldValueFormatter_Tests</c> walks <c>Enum.GetValues&lt;FieldDataType&gt;()</c>
/// so a new member that misses a branch reddens instead of shipping.
/// </para>
/// </summary>
public static class FieldValueFormatter
{
    /// <summary>
    /// Reconstructs the canonical <see cref="JsonElement"/> scalar for the <c>ExtractedFields</c> dictionary of
    /// the REST / MCP / DTO egress. The inverse of <c>DocumentExtractedField.SetValue</c>; the two round-trip.
    /// <para>
    /// An unknown type loud-fails and never emits an <c>Undefined</c> <see cref="JsonElement"/>: once assembled
    /// into a DTO, serializing that throws "Cannot write a JsonElement with ValueKind Undefined" and turns the
    /// whole document read into a 500.
    /// </para>
    /// </summary>
    public static JsonElement ToJsonElement(IFieldValueColumns columns, FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => JsonSerializer.SerializeToElement(columns.TextValue),
        FieldDataType.LongText => JsonSerializer.SerializeToElement(columns.LongTextValue),
        FieldDataType.Number => JsonSerializer.SerializeToElement(columns.NumberValue),
        FieldDataType.Boolean => JsonSerializer.SerializeToElement(columns.BooleanValue),
        FieldDataType.Date => JsonSerializer.SerializeToElement(
            columns.DateValue?.ToString(FieldValueFormats.Date, CultureInfo.InvariantCulture)),
        FieldDataType.DateTime => JsonSerializer.SerializeToElement(
            columns.DateTimeValue?.ToString(FieldValueFormats.DateTime, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };

    /// <summary>
    /// Renders one value row as the string an exported CSV / XLSX cell carries. Null means "this row holds no
    /// value for that type" — an empty cell, not the text "null".
    /// <para>
    /// Loud-fails on an unknown type for the same reason: a silently empty cell in a file an operator hands to an
    /// accountant is worse than an error.
    /// </para>
    /// </summary>
    public static string? ToCellString(IFieldValueColumns columns, FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => columns.TextValue,
        FieldDataType.LongText => columns.LongTextValue,
        FieldDataType.Number => columns.NumberValue?.ToString(FieldValueFormats.CellNumber, CultureInfo.InvariantCulture),
        FieldDataType.Boolean => columns.BooleanValue == null ? null : (columns.BooleanValue.Value ? "true" : "false"),
        FieldDataType.Date => columns.DateValue?.ToString(FieldValueFormats.Date, CultureInfo.InvariantCulture),
        FieldDataType.DateTime => columns.DateTimeValue?.ToString(FieldValueFormats.DateTime, CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, "Unsupported field data type.")
    };
}

/// <summary>
/// Format strings for rendering typed field-value columns.
/// <para>
/// <see cref="Date"/> and <see cref="DateTime"/> are <b>serialized contract</b>, not presentation: they are the
/// canonical shapes <c>DocumentFieldValue</c> requires on the way in, that the REST / MCP <c>ExtractedFields</c>
/// dictionary emits on the way out, and that the #411 field fingerprint hashes. Changing either value is a wire
/// break and a silent re-hash of every stored fingerprint. They are <c>const</c> so a host cannot widen them.
/// </para>
/// </summary>
public static class FieldValueFormats
{
    /// <summary>Canonical date shape, in and out. Frozen wire contract.</summary>
    public const string Date = "yyyy-MM-dd";

    /// <summary>Canonical offset-free date-time shape, in and out. Frozen wire contract.</summary>
    public const string DateTime = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>
    /// Minimal shape for a Number in an exported cell: integer 1000 -> "1000", decimal 10.50 -> "10.5", without
    /// the six trailing zeros of <c>decimal(38,6)</c>. Presentation only — deliberately <b>not</b> the fingerprint's
    /// number format, which keeps full precision so two values that differ beyond six decimals do not collide.
    /// </summary>
    public const string CellNumber = "0.######";
}
