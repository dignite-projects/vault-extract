using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Input for <c>IDocumentAppService.ResolveFieldValidationWarningsAsync</c> (#527 §9): the operator, after comparing
/// the source file, resolves the validation warnings on the named fields. This is a single resolution action — whether
/// the source data was confirmed correct as extracted or corrected by hand, the field edit itself is audited
/// separately, so no resolution-kind is carried here. The blocking review reason clears only once no active warning
/// remains.
/// </summary>
public class ResolveFieldValidationWarningsInput : IValidatableObject
{
    /// <summary>
    /// The immutable <c>FieldDefinition</c> ids (#207) whose validation warnings are resolved. Ids without an active
    /// warning are ignored. Bounded by <see cref="DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction"/> — a
    /// document carries at most that many warnings (one per field).
    /// </summary>
    [Required]
    public List<Guid> FieldDefinitionIds { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FieldDefinitionIds.Count > DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction)
        {
            yield return new ValidationResult(
                $"At most {DocumentFieldValidationWarningConsts.MaxWarningsPerExtraction} field validation warnings can be resolved in one request.",
                new[] { nameof(FieldDefinitionIds) });
        }
    }
}
