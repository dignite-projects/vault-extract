using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Structured result of one <see cref="FieldExtractionWorkflow.ExtractAsync"/> call (#527 §1): the extracted field
/// <see cref="Values"/> AND any <see cref="ValidationWarnings"/>, from a <b>single</b> LLM response — there is no second
/// call for validation.
/// <para>
/// A field that carries a validation warning still keeps its extracted value in <see cref="Values"/>: a warning is
/// never represented as a <c>null</c> value and is never mapped to <c>MissingRequiredFields</c> (that stays the
/// distinct "not extracted" signal). Missing / unparseable values continue to become <c>null</c> as before. The
/// App-layer caller (<c>FieldExtractionService</c>) persists the values and — resolving each warning's field name to
/// the immutable <c>FieldDefinition</c> Id (#207) — persists the warnings separately, keeping warning text out of field
/// values, search, export, and event payloads (#527 §11).
/// </para>
/// </summary>
public sealed record FieldExtractionWorkflowResult(
    IReadOnlyDictionary<string, JsonElement?> Values,
    IReadOnlyList<FieldValidationWarningResult> ValidationWarnings);

/// <summary>
/// One field validation warning as produced by the LLM (#527 §1), keyed by the field <b>name</b> — the workflow speaks
/// in field names, and the caller resolves the name to the immutable <c>FieldDefinition</c> Id (#207). The workflow has
/// already defensively normalized the untrusted model output (#527 §3): warnings for undeclared fields are discarded,
/// blank / malformed entries dropped, duplicates merged to one per field, the message truncated at a UTF-16 character
/// boundary, and the count capped — so the caller receives a clean, bounded set.
/// </summary>
public sealed record FieldValidationWarningResult(string FieldName, string Message);
