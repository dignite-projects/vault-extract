using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Dignite.Vault.Extract.Documents.Fields;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Single ExtractedFields field filter: one element of <see cref="GetDocumentListInput.FieldFilters"/>
/// and also the LLM-facing input element for the MCP search tool. A search can pass multiple filters;
/// they are combined with <c>AND</c> and all must be anchored to the same <c>documentTypeCode</c>. The
/// server resolves the declared field type from <c>(documentTypeCode, Name)</c> and the
/// <c>FieldDefinition</c>; callers do not pass a type. At least one of <see cref="Value"/> (equality)
/// or <see cref="Min"/> / <see cref="Max"/> (range) must be provided. Ranges are meaningful only for
/// Number / Date / DateTime fields; Text / Boolean support equality only.
/// </summary>
public class DocumentFieldFilter : IValidatableObject
{
    [Required]
    [RegularExpression(FieldDefinitionConsts.NamePattern)]
    [Description("Name of an extracted field to filter by. Must be a field defined on the document type.")]
    public string? Name { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Exact value the field must equal. Use this for String and Boolean fields "
        + "(which support equality only). Optional.")]
    public string? Value { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Inclusive lower bound for a field-value range. Only Number, Date and DateTime "
        + "fields support ranges (passing a range on a Text/Boolean field is rejected). Optional.")]
    public string? Min { get; set; }

    [StringLength(DocumentConsts.MaxSearchFieldValueLength)]
    [Description("Inclusive upper bound for a field-value range. Pairs with Min. Optional.")]
    public string? Max { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Require at least one value: equality, lower bound, or upper bound. Otherwise this is an
        // incomplete filter and must fail loudly through AbpValidationException instead of silently
        // degrading to "return all documents of this type".
        if (Value == null && Min == null && Max == null)
        {
            yield return new ValidationResult(
                "A field filter must specify a Value, or a Min/Max range.",
                new[] { nameof(Value), nameof(Min), nameof(Max) });
        }
    }
}
