using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Input for AI-polishing a field-extraction prompt (#447): the administrator's raw prompt text, which the
/// LLM normalizes into clean, well-formed Markdown. Capped at <see cref="FieldDefinitionConsts.MaxInteractivePromptInputLength"/>
/// to bound the interactive LLM call (the persisted <c>FieldDefinition.Prompt</c> itself is uncapped).
/// </summary>
public class FieldPromptPolishInput
{
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxInteractivePromptInputLength))]
    public string Prompt { get; set; } = default!;
}
