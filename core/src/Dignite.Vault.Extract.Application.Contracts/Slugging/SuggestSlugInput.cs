using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Input: the human-readable label entered by an admin in a creation form, usually an entity display
/// name in Chinese, Japanese, English, or any other language.
/// The server asks the LLM for an English translation, slugifies it, and returns a machine identifier
/// suggestion suitable for <see cref="FieldDefinition.Name"/> or <see cref="DocumentType.TypeCode"/>
/// that the admin may override manually.
/// </summary>
public class SuggestSlugInput
{
    // Generic input guardrail: prevents oversized text from being injected into the LLM prompt without
    // reusing any concrete entity DisplayName limit. See SlugSuggestionConsts.
    [Required]
    [DynamicStringLength(typeof(SlugSuggestionConsts), nameof(SlugSuggestionConsts.MaxLabelLength))]
    public string Label { get; set; } = default!;
}
