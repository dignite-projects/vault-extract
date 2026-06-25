using System;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Resolved single-field value query: one element of the <c>fieldQueries</c> list passed to
/// <see cref="IDocumentRepository.GetFieldMatchedIdsAsync"/>. One search can include multiple
/// queries. The repository compiles each element into one <c>Any</c> (EXISTS) predicate over
/// <see cref="Document.ExtractedFieldValues"/> and combines them with <c>AND</c>, matching structured
/// search convention where different fields narrow each other.
/// <para>
/// <see cref="FieldDefinitionId"/> and <see cref="FieldDataType"/> are resolved from
/// <c>FieldDefinition</c> by the caller layer (outbound adapter) before being filled here (#207:
/// internally match child rows by immutable Id, no longer by field-name string). The repository uses
/// <see cref="FieldDefinitionId"/> to locate the child row, then dispatches to the corresponding typed
/// column for plain equality / range comparisons. The repository does not depend on repositories for
/// other aggregates. <see cref="FieldName"/> is used only for readable error diagnostics and does not
/// participate in matching.
/// </para>
/// At least one of equality (<see cref="FieldValue"/>) or range (<see cref="FieldValueMin"/> /
/// <see cref="FieldValueMax"/>) must be provided. Otherwise the query is incomplete and the
/// repository fails closed with an empty result instead of degrading to "return all documents of this
/// type".
/// </summary>
public sealed record DocumentFieldQuery(
    Guid FieldDefinitionId,
    string FieldName,
    FieldDataType FieldDataType,
    string? FieldValue = null,
    string? FieldValueMin = null,
    string? FieldValueMax = null);
