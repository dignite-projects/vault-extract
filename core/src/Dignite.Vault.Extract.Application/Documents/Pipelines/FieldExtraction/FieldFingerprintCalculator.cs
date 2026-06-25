using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Computes <see cref="Document.FieldFingerprint"/> (#411): a stable SHA-256 over a document type's
/// <b>unique-key field values</b> (the <see cref="FieldDefinition.IsUniqueKey"/> set), used to detect duplicate
/// re-uploads of the same business entity (e.g. an invoice number + issue date + amount).
/// <para>
/// Determinism / precision rules:
/// <list type="bullet">
///   <item>Unique-key fields are taken in a canonical order (by <see cref="FieldDefinition.Id"/>), and each field's
///   value(s) in <see cref="DocumentExtractedField.Order"/> order, so the same extracted set always hashes the same
///   regardless of row enumeration order.</item>
///   <item>Values are normalized per <see cref="FieldDataType"/> — text is trimmed, internal whitespace folded, and
///   lower-cased; numbers strip trailing zeros (100 == 100.00); dates/datetimes use an invariant ISO form — so
///   cosmetic differences between two scans of the same document do not defeat the match.</item>
///   <item>A <b>partial key</b> (the type declares unique-key fields but at least one has no extracted / non-blank
///   value) returns <c>null</c>: an incomplete key would collide unrelated documents, so it is deliberately not
///   fingerprinted (fewer false positives, at the cost of missing a duplicate whose key field failed to extract).</item>
///   <item>No unique-key fields configured returns <c>null</c> — duplicate detection is opt-in per type.</item>
/// </list>
/// </para>
/// Pure / deterministic (no <c>IClock</c> / DI), so it is a static helper and trivially unit-testable.
/// </summary>
public static class FieldFingerprintCalculator
{
    // Unambiguous separators (ASCII unit / record separators) that cannot appear in normalized values, so distinct
    // field/value boundaries never alias into the same canonical string.
    private const char ValueSeparator = '\u001F';
    private const char FieldSeparator = '\u001E';

    /// <summary>
    /// Returns the fingerprint for the given extracted values + their definitions, or <c>null</c> when the type has
    /// no unique-key fields or the key is partial. <paramref name="definitions"/> must be the document type's current
    /// field definitions (so <see cref="FieldDefinition.DataType"/> matches the stored typed columns).
    /// </summary>
    public static string? Compute(
        IReadOnlyCollection<DocumentExtractedField> values,
        IReadOnlyCollection<FieldDefinition> definitions)
    {
        var uniqueKeyDefinitions = definitions
            .Where(d => d.IsUniqueKey)
            .OrderBy(d => d.Id)
            .ToList();

        if (uniqueKeyDefinitions.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var definition in uniqueKeyDefinitions)
        {
            var fieldValues = values
                .Where(v => v.FieldDefinitionId == definition.Id)
                .OrderBy(v => v.Order)
                .ToList();

            if (fieldValues.Count == 0)
            {
                // Partial key: a declared unique-key field has no value -> do not fingerprint.
                return null;
            }

            builder.Append(definition.Id.ToString("N"));
            builder.Append('=');
            for (var i = 0; i < fieldValues.Count; i++)
            {
                var canonical = Canonicalize(fieldValues[i], definition.DataType);
                if (canonical == null)
                {
                    // A row exists but its value is blank/absent for its type -> still a partial key.
                    return null;
                }

                if (i > 0)
                {
                    builder.Append(ValueSeparator);
                }

                builder.Append(canonical);
            }

            builder.Append(FieldSeparator);
        }

        return ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string? Canonicalize(DocumentExtractedField row, FieldDataType dataType) => dataType switch
    {
        FieldDataType.Text => NormalizeText(row.TextValue),
        FieldDataType.LongText => NormalizeText(row.LongTextValue),
        FieldDataType.Number => row.NumberValue?.ToString("0.############################", CultureInfo.InvariantCulture),
        FieldDataType.Boolean => row.BooleanValue switch { true => "true", false => "false", null => null },
        FieldDataType.Date => row.DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        FieldDataType.DateTime => row.DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? NormalizeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Fold all runs of whitespace to a single space, trim, then lower-case invariantly so two scans of the same
        // value (e.g. "INV 001" vs "inv  001") match.
        var folded = string.Join(' ', raw.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
        return folded.ToLowerInvariant();
    }
}
