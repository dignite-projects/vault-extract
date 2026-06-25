using System;
using System.Text.Json;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Typed single-field value: input element for <see cref="Document.SetFields"/> (field architecture
/// v2 / #207).
/// <para>
/// Constructed by the App layer (<c>FieldExtractionEventHandler</c> /
/// <c>DocumentAppService.UpdateExtractedFieldsAsync</c>) after validation succeeds. The App layer
/// receives the raw <see cref="JsonElement"/> submitted by the LLM / operator plus the owning
/// <c>FieldDefinition</c>, including <see cref="FieldDefinitionId"/> and <see cref="DataType"/>,
/// verifies that <paramref name="Value"/> aligns with <paramref name="DataType"/> through
/// <c>ExtractedFieldValueValidator</c>, then passes this to the aggregate root. The aggregate uses it
/// to construct / update <see cref="DocumentExtractedField"/>, reconciling by
/// <see cref="FieldDefinitionId"/> and centralizing JsonElement-to-typed conversion inside the child
/// entity.
/// </para>
/// <para>
/// <paramref name="Value"/> must be a canonical JSON <b>scalar</b> aligned with
/// <paramref name="DataType"/>: numbers are bare JSON numbers, booleans are true/false, Date is a
/// <c>"yyyy-MM-dd"</c> string, and DateTime is an offset-free <c>"yyyy-MM-ddThh:mm:ss"</c> string.
/// Misaligned values are filtered out / fail loudly in the App layer and never reach this point.
/// </para>
/// <para>
/// <paramref name="Order"/> (#212) is the 0-based position of this value inside its field's
/// multi-value set and participates in the <see cref="DocumentExtractedField"/> composite primary key
/// <c>(DocumentId, FieldDefinitionId, Order)</c>. Single-value fields are always 0. JSON arrays for
/// multi-value text fields (<c>FieldDefinition.AllowMultiple</c>) are split by the App layer into
/// multiple records here (<c>Order = 0,1,2...</c>, one scalar per element). <see cref="Document.SetFields"/>
/// reconciles by <c>(FieldDefinitionId, Order)</c>.
/// </para>
/// </summary>
public sealed record DocumentFieldValue(Guid FieldDefinitionId, FieldDataType DataType, JsonElement Value, int Order = 0);
