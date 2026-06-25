using System;
using System.Globalization;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Single source of truth for deciding whether a <see cref="JsonElement"/> value matches the
/// declared <see cref="FieldDataType"/>.
/// Only values that pass this validation are split into the typed columns on
/// <c>DocumentExtractedField</c> (Issue #206).
/// <para>
/// Both write paths share this validator so persisted field values are always either type-compatible
/// or null:
/// <list type="bullet">
///   <item><b>Operator edits</b> (<c>DocumentAppService.UpdateExtractedFieldsAsync</c>): interactive
///   path; invalid values throw <see cref="ExtractErrorCodes.ExtractedField.InvalidValue"/> so the
///   operator can correct them.</item>
///   <item><b>LLM extraction</b> (<c>FieldExtractionWorkflow</c>): background non-interactive path;
///   invalid values are persisted as null plus a log entry. Normalization belongs in the prompt and
///   the AI should output canonical shapes; this validator is the last guardrail.</item>
/// </list>
/// Clean field values let typed-column queries in <c>GetFieldMatchedIdsAsync</c> (plain equality /
/// range comparison) operate on trusted data. They also ensure that
/// <c>DocumentExtractedField.SetValue</c> can split JSON into typed columns without throwing because
/// of a type mismatch.
/// </para>
/// <para>
/// Strict semantics with no coercion: the declared type promises that the value can be expressed as
/// that JSON type. Number fields must be JSON numbers, Boolean fields must be JSON true/false, and
/// Date fields must be ISO-8601 strings. Free text has two lanes:
/// <see cref="FieldDataType.Text"/> for structured short values with length less than or equal to
/// <see cref="DocumentExtractedFieldConsts.MaxTextValueLength"/> (same source as the persisted column
/// length, included in the composite index, equality-queryable, #209), or
/// <see cref="FieldDataType.LongText"/> for long content such as summaries / descriptions with length
/// less than or equal to <see cref="DocumentExtractedFieldConsts.MaxLongTextValueLength"/> (stored in
/// an nvarchar(max) column, not indexed, and not queryable). The real full-text payload still belongs
/// to Document.Markdown, not to type-bound fields.
/// </para>
/// </summary>
internal static class ExtractedFieldValueValidator
{
    /// <summary>
    /// Multi-value-aware validation (#212). When <paramref name="allowMultiple"/> is true (only for
    /// <see cref="FieldDataType.Text"/> fields, enforced by the <c>FieldDefinition</c> entity layer),
    /// <paramref name="value"/> must be a JSON array and every element must be a valid scalar value.
    /// Empty arrays are valid (zero rows). When false, this falls back to scalar validation, matching
    /// the original <see cref="IsValid(JsonElement, FieldDataType)"/>.
    /// </summary>
    public static bool IsValid(JsonElement value, FieldDataType dataType, bool allowMultiple)
    {
        if (!allowMultiple)
        {
            return IsValid(value, dataType);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // Hard result cap (#212): more than MaxMultiValueCount values makes the whole group invalid,
        // preventing malicious documents from inducing the LLM to emit huge arrays and inflate rows.
        // The LLM path stores null plus a log entry; operator edits fail loudly. Schema maxItems is a
        // soft hint, while this is the hard guardrail.
        if (value.GetArrayLength() > DocumentExtractedFieldConsts.MaxMultiValueCount)
        {
            return false;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (!IsValid(element, dataType))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValid(JsonElement value, FieldDataType dataType)
    {
        return dataType switch
        {
            FieldDataType.Text => value.ValueKind == JsonValueKind.String &&
                                    (value.GetString() ?? string.Empty).Length <= DocumentExtractedFieldConsts.MaxTextValueLength,
            FieldDataType.LongText => value.ValueKind == JsonValueKind.String &&
                                      (value.GetString() ?? string.Empty).Length <= DocumentExtractedFieldConsts.MaxLongTextValueLength,
            FieldDataType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            FieldDataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldDataType.Date => IsValidDateString(value),
            FieldDataType.DateTime => IsValidDateTimeString(value),
            _ => false
        };
    }

    private static bool IsValidDateString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParseExact(
                   value.GetString(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static bool IsValidDateTimeString(JsonElement value)
    {
        // Accept only offset-free wall-clock ISO-8601 values (YYYY-MM-DDThh:mm:ss). .NET converts
        // strings with offsets / Z to the local time zone, which conflicts with the wall-clock
        // semantics of the datetime2 storage / query column and makes comparisons drift with the
        // server time zone. DateTimeKind.Unspecified means the input did not carry time-zone
        // information. Zoned instants are outside the channel DateTime field contract; downstream
        // business aggregate roots should own those values when needed.
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed) &&
               parsed.Kind == DateTimeKind.Unspecified;
    }
}
