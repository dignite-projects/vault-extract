using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Input element for <see cref="Document.ReplaceFieldValidationWarnings"/> (#527 §4): one validation warning for one
/// type-bound field, keyed by the immutable <see cref="FieldDefinitionId"/> (#207, no field-name snapshot).
/// <para>
/// Constructed by the App layer (<c>FieldExtractionService</c>) after it resolves the model's field name to a current
/// <c>FieldDefinition</c>, discards warnings for undeclared / stale fields, deduplicates by field, truncates the message
/// at a valid UTF-16 boundary, and caps the count (#527 §3). The aggregate builds / reconciles the persisted
/// <see cref="DocumentFieldValidationWarning"/> rows from this set and couples the blocking review bit. Mirrors
/// <see cref="DocumentFieldValue"/> for field values.
/// </para>
/// </summary>
public sealed record FieldValidationWarning(Guid FieldDefinitionId, string Message);
