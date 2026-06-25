using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Review;

/// <summary>
/// Review-state evaluator (#284): pure functional domain service that only computes the
/// MissingRequiredFields dimension.
/// <para>
/// No repository dependency: accepts two field-definition ID sets and returns a boolean, so unit tests
/// need no mocks. Callers (field extraction handler / operator-completion AppService) invoke it
/// inside the same UoW where required / extracted sets are already loaded, then call
/// <see cref="Document.SetReviewReason"/> based on the result.
/// </para>
/// <para>
/// The classification dimension (<see cref="DocumentReviewReasons.UnresolvedClassification"/>) is
/// handled inline by <see cref="Document"/> write methods because it mirrors
/// <c>DocumentTypeId.HasValue</c> and those methods already have typeId context. It does not enter
/// this evaluator, avoiding unnecessary dependencies and repeated queries.
/// </para>
/// </summary>
public class ReviewStateEvaluator : ITransientDependency
{
    /// <summary>
    /// Whether required fields are missing: some <c>IsRequired</c> field definition is absent from the
    /// set of fields with extracted values.
    /// </summary>
    /// <param name="requiredFieldDefinitionIds">Definition IDs where <c>FieldDefinition.IsRequired == true</c> under this document type.</param>
    /// <param name="extractedFieldDefinitionIds">Distinct <c>FieldDefinitionId</c> values extracted for this document.</param>
    public virtual bool MissingRequiredFieldsPresent(
        IReadOnlyCollection<Guid> requiredFieldDefinitionIds,
        IReadOnlyCollection<Guid> extractedFieldDefinitionIds)
    {
        if (requiredFieldDefinitionIds.Count == 0)
        {
            return false;
        }

        var extracted = extractedFieldDefinitionIds as ISet<Guid> ?? extractedFieldDefinitionIds.ToHashSet();
        return requiredFieldDefinitionIds.Any(id => !extracted.Contains(id));
    }
}
