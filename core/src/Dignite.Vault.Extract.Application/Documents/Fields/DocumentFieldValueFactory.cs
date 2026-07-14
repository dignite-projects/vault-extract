using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Expands one field value, already validated by <see cref="ExtractedFieldValueValidator"/>, into one
/// or more <see cref="DocumentFieldValue"/> rows (#212). Shared by both write paths: LLM extraction
/// (<c>FieldExtractionService</c>) and operator edits
/// (<c>DocumentAppService.UpdateExtractedFieldsAsync</c>):
/// <list type="bullet">
///   <item>Single-value fields (<c>allowMultiple == false</c>): scalar <paramref name="value"/> becomes
///   one row with <c>Order = 0</c>.</item>
///   <item>Multi-value text fields (<c>allowMultiple == true</c>): JSON array
///   <paramref name="value"/> becomes one row per element, with <c>Order</c> following array order
///   0,1,2...; empty array becomes zero rows.</item>
/// </list>
/// Callers must already have passed <c>IsValid(value, dataType, allowMultiple)</c>; the multi-value
/// path assumes <paramref name="value"/> is an array.
/// </summary>
internal static class DocumentFieldValueFactory
{
    public static IEnumerable<DocumentFieldValue> Expand(
        Guid fieldDefinitionId, FieldDataType dataType, bool allowMultiple, JsonElement value)
    {
        if (!allowMultiple)
        {
            yield return new DocumentFieldValue(fieldDefinitionId, dataType, value, 0);
            yield break;
        }

        var order = 0;
        foreach (var element in value.EnumerateArray())
        {
            // Clone to detach from the original JsonDocument buffer and own the value independently,
            // matching the lifetime safety used by workflow-side root.Clone().
            yield return new DocumentFieldValue(fieldDefinitionId, dataType, element.Clone(), order++);
        }
    }
}
