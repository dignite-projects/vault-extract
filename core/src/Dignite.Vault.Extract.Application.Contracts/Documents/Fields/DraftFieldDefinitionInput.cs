using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Input for "draft field metadata from prompt" (issue #264). Admin provides extraction instructions
/// (prompt) as the primary input; the backend uses the LLM to <b>draft</b> the remaining field
/// metadata once, then the admin reviews / edits each value before saving.
/// </summary>
public class DraftFieldDefinitionInput
{
    /// <summary>
    /// Extraction instructions: the only input signal for drafting. Length limit reuses
    /// <see cref="FieldDefinitionConsts.MaxPromptLength"/> because this value eventually lands in
    /// <c>FieldDefinition.Prompt</c>, sharing the same guardrail.
    /// </summary>
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxPromptLength))]
    public string Prompt { get; set; } = default!;

    /// <summary>
    /// true = new field: the draft <b>also suggests</b> machine key
    /// <see cref="FieldDefinitionDraftDto.Name"/>. false = editing an existing field:
    /// <c>Name</c> is a contract-level frozen identity key (#207 / downstream contract ID /
    /// ExtractedFields dictionary key), and the draft <b>does not touch it</b>. The server always
    /// returns empty Name to avoid silently churning downstream contract keys (issue #264 guardrail 1).
    /// </summary>
    public bool ForNewField { get; set; }
}
