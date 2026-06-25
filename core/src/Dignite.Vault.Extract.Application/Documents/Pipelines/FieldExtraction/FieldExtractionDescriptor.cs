using System;
using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Workflow-internal DTO: runtime description for field extraction, decoupled from the persisted
/// <see cref="FieldDefinition"/> entity.
/// Field schema v2 plus interpretation X: one LLM call runs only one layer of field definitions, selected
/// by Document.TenantId as host versus tenant. Therefore the descriptor does not need a source marker:
/// the descriptor list itself is a single-layer schema.
/// <para>
/// <see cref="FieldDefinitionId"/> (#207) travels with the descriptor: LLM output is read back by
/// <see cref="Name"/>, the prompt schema key, while persisted field value rows are constructed by
/// <see cref="FieldDefinitionId"/> as the immutable internal association. Name renames do not affect it.
/// </para>
/// </summary>
public sealed record FieldExtractionDescriptor(
    Guid FieldDefinitionId,
    string Name,
    string? Prompt,
    FieldDataType DataType,
    bool IsRequired,
    bool AllowMultiple);
