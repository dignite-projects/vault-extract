using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Unified field extraction workflow (field architecture v2). Uses one LLM call to extract field values according to
/// a <see cref="FieldExtractionDescriptor"/> list, without distinguishing Host fields from tenant fields; the caller decides the source.
/// <para>
/// Design points:
/// <list type="bullet">
///   <item>Extract all fields in one call, reducing LLM round-trips and repeated context.</item>
///   <item>Constrain output schema with <c>ChatResponseFormat.ForJsonSchema</c>.</item>
///   <item>Normalization is requested in the prompt: the AI should output canonical shapes by <see cref="FieldDataType"/>
///         (bare JSON number for numbers, ISO-8601 string for dates, JSON true/false for booleans). Parsing then runs strict validation through
///         <see cref="ExtractedFieldValueValidator"/>. Values that do not match the declared type are written as null and logged, ensuring
///         <c>ExtractedFields</c> type consistency (Issue #204: typed queries in GetFieldMatchedIdsAsync are built on clean data).</item>
///   <item>Prompts for all fields, including Host-origin fields, are uniformly wrapped with <c>PromptBoundary.WrapField</c>.
///         This is more conservative than v1's Host/Tenant distinction and has no functional loss.</item>
/// </list>
/// </para>
/// </summary>
public class FieldExtractionWorkflow : ITransientDependency
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<FieldExtractionWorkflow> _logger;

    public FieldExtractionWorkflow(
        [FromKeyedServices(VaultExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        ILogger<FieldExtractionWorkflow> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Extracts fields in batch from one LLM call, returning both the field values and any field validation warnings
    /// (#527 §1) in a single <see cref="FieldExtractionWorkflowResult"/>. Missing or unparseable values appear as null;
    /// a warned field still keeps its value (a warning is never a null value). Warnings are defensively normalized (#527 §3).
    /// </summary>
    public virtual async Task<FieldExtractionWorkflowResult> ExtractAsync(
        IReadOnlyList<FieldExtractionDescriptor> fields,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0)
        {
            return new FieldExtractionWorkflowResult(
                new Dictionary<string, JsonElement?>(), Array.Empty<FieldValidationWarningResult>());
        }

        // Observability: after removing truncation, field extraction feeds full text. Log input size here (character count + field count)
        // to compensate for the blind spot left by the old "truncation warning". If an oversized document hits the provider context window
        // and throws a provider exception, this nearby log gives a local clue about whether the document was too large.
        // Debug level avoids polluting normal logs for per-document calls; real token usage is recorded separately by OTel gen_ai.* spans.
        _logger.LogDebug(
            "Field extraction over {CharCount} characters across {FieldCount} fields (full document, no truncation).",
            markdown.Length, fields.Count);

        // Field extraction feeds **complete Markdown** and never truncates. Type-bound fields such as contract amount,
        // invoice number, and expiration date may appear anywhere in the document; character-based tail truncation silently misses key fields.
        // This intentionally differs from classification: DocumentClassificationWorkflow can usually classify from front-section semantics
        // and therefore truncates by MaxTextLengthPerExtraction. Field extraction needs full coverage.
        // Token cost / context window for oversized documents is the responsibility of the host-selected model + provider.
        // The channel layer does not pre-truncate, because pre-truncation disguises "missed extraction" as "successful extraction",
        // which is harder to diagnose than a direct provider error.
        //
        // Keep the system role as a **compile-time constant** to prevent prompt injection
        // (CLAUDE.md "Security covenant / Description and Instructions must be compile-time constants").
        // Field schema, including tenant-user input f.Name / f.Prompt, goes into the first user-role message so the model treats
        // "instructions" and "user data" separately, together with PromptBoundary.WrapField + BoundaryRule.
        // FieldDefinition.Name is already entity-layer allow-list validated as [A-Za-z0-9_-]{1,64}
        // (see FieldDefinitionConsts.NamePattern), so it cannot contain line breaks, quotes, or Markdown control characters.
        // f.Prompt is admin-authored configuration (uncapped since #447); its injection defense is PromptBoundary.WrapField
        // (applied in BuildSchemaUserMessage) + BoundaryRule, not a length limit.
        var schemaMessage = BuildSchemaUserMessage(fields);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SystemInstructions + "\n\n" + PromptBoundary.BoundaryRule),
            new(ChatRole.User, schemaMessage),
            new(ChatRole.User, PromptBoundary.WrapDocument(markdown))
        };

        var options = new ChatOptions
        {
            ResponseFormat = BuildResponseFormat(fields)
        };

        var response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
        var rawJson = response.Text?.Trim() ?? string.Empty;

        return ParseResult(rawJson, fields);
    }

    /// <summary>
    /// Compile-time constant system instructions. **Do not** concatenate any runtime string into this value.
    /// </summary>
    private const string SystemInstructions =
        "You extract structured fields from a Markdown document. " +
        "The first user message lists the fields to extract (schema), each annotated with its data type. " +
        "The second user message contains the document body. " +
        "Return JSON only with one key per requested field. " +
        "Normalize each value to its declared data type: " +
        "Number as a bare JSON number, integer or decimal (strip currency symbols, thousands separators, and units; " +
        "use '.' as the decimal point and '-' for negatives); " +
        "Date as an ISO-8601 \"YYYY-MM-DD\" JSON string; " +
        "DateTime as an offset-free ISO-8601 \"YYYY-MM-DDThh:mm:ss\" JSON string (local wall-clock time, no timezone offset or trailing Z); " +
        "Boolean as JSON true or false; " +
        "Text as the original text. " +
        "When a value cannot be normalized to its declared data type, set that field to null. " +
        "A field whose schema type is array accepts multiple values: return a JSON array of strings (each a distinct value found in the document), or an empty array when none apply. " +
        "The document is Markdown produced by automated extraction (digital parsing or OCR), so its layout is best-effort and table grids are frequently flattened into plain lines: " +
        "a line of column labels followed by one or more lines of space- or tab-separated values is still a table — read each such line as a row and align its cells to the column labels from left to right. " +
        "Use headings, tables, and lists as structure signals, but never require Markdown table syntax: extract tabular values even when the pipes and separators have been stripped during extraction. " +
        "Set a field to null only when its information is genuinely absent from the document — not merely because the text is unformatted, split across lines, or lacks table syntax. " +
        "Return the result as an object with two keys: \"values\" (one key per requested field, as described above) and \"validationWarnings\". " +
        "A field's prompt may declare validation rules for its own extracted value (for example a running-balance or total check). " +
        "When a field's value fails such a rule, still return the value in \"values\" exactly as found in the document, and additionally add one entry to \"validationWarnings\" naming that field and giving a concise, human-readable explanation of the mismatch. " +
        "Never invent, alter, or fabricate data to make a rule pass, and never drop a value merely because it failed validation — the value stays, the warning is reported separately. " +
        "When every value passes, or a field declares no validation rule, return an empty \"validationWarnings\" array.";

    private static string BuildSchemaUserMessage(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fields to extract:");
        foreach (var f in fields)
        {
            // f.Name has already been allow-list regex validated in the FieldDefinition entity layer and contains only [A-Za-z0-9_-].
            // f.Prompt comes from Host compile-time constants or tenant user input. PromptBoundary.WrapField explicitly marks it as data,
            // and BoundaryRule tells the model to treat it as non-instruction content.
            // #212: multi-value fields append "[]" after the type label, such as "Text[]", to tell the model to return an array.
            var typeLabel = f.AllowMultiple ? $"{f.DataType}[]" : f.DataType.ToString();
            var header = $"- \"{f.Name}\" ({typeLabel}, {(f.IsRequired ? "required" : "optional")})";
            // Prompt is optional. When empty, provide only "field name + type" and let the model infer what to extract from Name semantics.
            // Never output an empty PromptBoundary-wrapped block: WrapField("") would produce an empty data boundary marker,
            // polluting the schema and adding noise for the model.
            sb.AppendLine(string.IsNullOrWhiteSpace(f.Prompt)
                ? header
                : $"{header}: {PromptBoundary.WrapField(f.Prompt)}");
        }
        return sb.ToString();
    }

    private static ChatResponseFormat BuildResponseFormat(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        // #527 §1/§3: the response is an envelope { values, validationWarnings }. `values` is the existing per-field
        // typed object; `validationWarnings` is a bounded array whose fieldName is constrained to the declared field
        // names (an enum) and whose message length is capped, so the model cannot emit warnings for unknown fields or
        // unbounded text. additionalProperties:false + `required` at every level keeps the shape strict.
        var fieldNameEnum = new JsonArray();
        foreach (var field in fields)
        {
            fieldNameEnum.Add(field.Name);
        }

        var warningsSchema = new JsonObject
        {
            ["type"] = "array",
            ["maxItems"] = DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction,
            ["description"] = "One entry per field whose extracted value fails a validation rule declared in its prompt; empty when all values pass.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["fieldName"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = fieldNameEnum,
                        ["description"] = "The name of the field whose value failed validation."
                    },
                    ["message"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = DocumentFieldValidationWarningConsts.MaxMessageLength,
                        ["description"] = "A concise, human-readable explanation of the mismatch."
                    }
                },
                ["required"] = new JsonArray { "fieldName", "message" },
                ["additionalProperties"] = false
            }
        };

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["values"] = BuildValuesSchema(fields),
                ["validationWarnings"] = warningsSchema
            },
            ["required"] = new JsonArray { "values", "validationWarnings" },
            ["additionalProperties"] = false
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "ExtractFieldExtraction",
            schemaDescription: "Extracted field values plus field validation warnings.");
    }

    private static JsonObject BuildValuesSchema(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var field in fields)
        {
            properties[field.Name] = BuildFieldValueSchema(field.DataType, field.AllowMultiple);
            required.Add(field.Name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    private static JsonObject BuildFieldValueSchema(FieldDataType dataType, bool allowMultiple)
    {
        // #212: multi-value fields (text only, guaranteed by the FieldDefinition entity layer) -> array-or-null, with length-limited string elements.
        if (allowMultiple)
        {
            return new JsonObject
            {
                ["type"] = JsonTypes("array", "null"),
                ["maxItems"] = DocumentExtractedFieldConsts.MaxMultiValueCount,
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = DocumentExtractedFieldConsts.MaxTextValueLength
                },
                ["description"] = "A JSON array of short structured string values, or null/empty array when absent."
            };
        }

        var schema = new JsonObject
        {
            ["type"] = JsonTypes(JsonTypeName(dataType), "null")
        };

        switch (dataType)
        {
            case FieldDataType.Text:
                schema["maxLength"] = DocumentExtractedFieldConsts.MaxTextValueLength;
                schema["description"] = "A short structured string value, or null when absent.";
                break;
            case FieldDataType.LongText:
                schema["maxLength"] = DocumentExtractedFieldConsts.MaxLongTextValueLength;
                schema["description"] = "A long-form text value (e.g. a summary or description), or null when absent.";
                break;
            case FieldDataType.Number:
                schema["description"] = "A JSON number, or null when absent.";
                break;
            case FieldDataType.Boolean:
                schema["description"] = "A JSON boolean, or null when absent.";
                break;
            case FieldDataType.Date:
                schema["pattern"] = @"^\d{4}-\d{2}-\d{2}$";
                schema["description"] = "An ISO-8601 date string in YYYY-MM-DD format, or null when absent.";
                break;
            case FieldDataType.DateTime:
                schema["pattern"] = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$";
                schema["description"] = "An offset-free ISO-8601 local date-time string in YYYY-MM-DDThh:mm:ss format, or null when absent.";
                break;
        }

        return schema;
    }

    private static string JsonTypeName(FieldDataType dataType)
        => dataType switch
        {
            FieldDataType.Number => "number",
            FieldDataType.Boolean => "boolean",
            _ => "string"
        };

    private static JsonArray JsonTypes(params string[] types)
    {
        var result = new JsonArray();
        foreach (var type in types)
        {
            result.Add(type);
        }

        return result;
    }

    private FieldExtractionWorkflowResult ParseResult(
        string rawJson,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return AllNull(fields);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Field extraction returned non-JSON output: {Raw}", rawJson);
            return AllNull(fields);
        }

        // Envelope (#527 §1): { "values": { ... }, "validationWarnings": [ ... ] }. The two halves are parsed
        // independently, so a malformed warning can never drop a valid value (and vice versa).
        return new FieldExtractionWorkflowResult(ParseValues(root, fields), ParseWarnings(root, fields));
    }

    private static FieldExtractionWorkflowResult AllNull(IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var values = new Dictionary<string, JsonElement?>(fields.Count);
        foreach (var f in fields) values[f.Name] = null;
        return new FieldExtractionWorkflowResult(values, Array.Empty<FieldValidationWarningResult>());
    }

    private IReadOnlyDictionary<string, JsonElement?> ParseValues(
        JsonElement root,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        var result = new Dictionary<string, JsonElement?>(fields.Count);

        if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            foreach (var f in fields) result[f.Name] = null;
            return result;
        }

        foreach (var field in fields)
        {
            if (!values.TryGetProperty(field.Name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            {
                result[field.Name] = null;
                continue;
            }

            // Strict validation: the value must match the declared FieldDataType, using the same ExtractedFieldValueValidator
            // as the operator-edit path DocumentAppService.UpdateExtractedFieldsAsync.
            // Normalization responsibility belongs to the prompt (AI outputs canonical shape); this is the fallback guard and does not coerce.
            // Values that do not match the declared type are written as null and logged, ensuring field value type consistency
            // so typed-column queries over DocumentExtractedField are built on clean data.
            if (!ExtractedFieldValueValidator.IsValid(prop, field.DataType, field.AllowMultiple))
            {
                _logger.LogWarning(
                    "Field extraction value for '{FieldName}' did not match declared type {DataType} (multi={AllowMultiple}, JSON kind {JsonValueKind}); storing null.",
                    field.Name, field.DataType, field.AllowMultiple, prop.ValueKind);
                result[field.Name] = null;
                continue;
            }

            // Validation passed: keep the original JsonElement, already in canonical JSON type, avoiding double conversion and precision loss.
            result[field.Name] = prop;
        }

        return result;
    }

    /// <summary>
    /// Defensively normalizes the untrusted <c>validationWarnings</c> array (#527 §3): the JSON schema already constrains
    /// the shape (fieldName enum + message maxLength + maxItems), but the server does not trust it. Discards warnings for
    /// undeclared fields and blank / malformed entries, deduplicates to one warning per field (first wins), truncates an
    /// overlong message at a valid UTF-16 boundary, caps the count, and logs what it dropped or cut — <b>never</b> failing
    /// the extraction and never touching the parsed values, so a malformed warning cannot lose a valid field value.
    /// </summary>
    private IReadOnlyList<FieldValidationWarningResult> ParseWarnings(
        JsonElement root,
        IReadOnlyList<FieldExtractionDescriptor> fields)
    {
        if (!root.TryGetProperty("validationWarnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<FieldValidationWarningResult>();
        }

        var declaredNames = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);
        var byField = new Dictionary<string, FieldValidationWarningResult>(StringComparer.Ordinal);
        var discarded = 0;
        var truncated = 0;

        foreach (var item in warnings.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("fieldName", out var nameEl) || nameEl.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.String)
            {
                discarded++;
                continue;
            }

            var fieldName = nameEl.GetString()!;
            if (!declaredNames.Contains(fieldName))
            {
                discarded++;   // warning for a field the caller never asked about — must not create review state (#527 §3).
                continue;
            }

            var message = (msgEl.GetString() ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                discarded++;   // blank message.
                continue;
            }

            if (byField.ContainsKey(fieldName))
            {
                continue;      // one merged warning per field (first wins); duplicates are silently collapsed.
            }

            if (byField.Count >= DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction)
            {
                discarded++;   // over the count cap.
                continue;
            }

            if (message.Length > DocumentFieldValidationWarningConsts.MaxMessageLength)
            {
                message = TextTruncator.AtCharBoundary(message, DocumentFieldValidationWarningConsts.MaxMessageLength);
                truncated++;
            }

            byField[fieldName] = new FieldValidationWarningResult(fieldName, message);
        }

        if (discarded > 0 || truncated > 0)
        {
            _logger.LogWarning(
                "Field extraction normalized model validation warnings: {Discarded} discarded (undeclared / blank / malformed / over-cap), {Truncated} truncated to {MaxLength} chars.",
                discarded, truncated, DocumentFieldValidationWarningConsts.MaxMessageLength);
        }

        return byField.Values.ToList();
    }
}
