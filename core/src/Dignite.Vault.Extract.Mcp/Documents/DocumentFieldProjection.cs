using System.Collections.Generic;
using System.Text.Json;
using Dignite.Vault.Extract.Ai;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// LLM-facing projection logic for ExtractedFields. Shared by the search tool and get_document tool so
/// PromptBoundary wrapping rules are exactly consistent in both places, with one implementation source
/// for the safety rule.
/// </summary>
internal static class DocumentFieldProjection
{
    /// <summary>
    /// Converts document ExtractedFields, raw <see cref="JsonElement"/> values, into the LLM-facing
    /// projection while preserving declared types: structured values such as numbers / booleans pass
    /// through raw; String values are wrapped with <c>PromptBoundary.WrapField</c> to prevent indirect
    /// prompt injection; JSON null values are skipped. Returns null when all values are skipped or no
    /// fields exist.
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonElement>? Project(
        IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (fields is not { Count: > 0 })
        {
            return null;
        }

        var projected = new Dictionary<string, JsonElement>(fields.Count);
        foreach (var pair in fields)
        {
            switch (pair.Value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    continue;
                case JsonValueKind.String:
                    projected[pair.Key] = JsonSerializer.SerializeToElement(
                        PromptBoundary.WrapField(pair.Value.GetString()));
                    break;
                case JsonValueKind.Array:
                    // Multi-value fields (#212): wrap each element individually because every String
                    // element is user-derived free text. Skip empty arrays.
                    var items = new List<JsonElement>();
                    foreach (var element in pair.Value.EnumerateArray())
                    {
                        switch (element.ValueKind)
                        {
                            case JsonValueKind.Null:
                            case JsonValueKind.Undefined:
                                continue;
                            case JsonValueKind.String:
                                items.Add(JsonSerializer.SerializeToElement(
                                    PromptBoundary.WrapField(element.GetString())));
                                break;
                            default:
                                items.Add(element);
                                break;
                        }
                    }
                    if (items.Count > 0)
                    {
                        projected[pair.Key] = JsonSerializer.SerializeToElement(items);
                    }
                    break;
                default:
                    projected[pair.Key] = pair.Value;
                    break;
            }
        }

        return projected.Count > 0 ? projected : null;
    }
}
