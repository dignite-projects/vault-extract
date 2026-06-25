namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Output of "draft from prompt" (issue #264): AI-drafted field metadata <b>draft</b>.
/// <para>
/// This is a <b>one-time draft, not continuous truth derivation</b>. After fields land in frontend
/// form controls, users can still review / modify every item before saving. <c>Name</c> is populated
/// only when <see cref="DraftFieldDefinitionInput.ForNewField"/> is true, already sanitized as a
/// whitelist slug. When editing an existing field it is always an empty string (guardrail 1:
/// contract-level identity key is frozen and not overwritten by AI).
/// </para>
/// <para>
/// Any field may be <b>empty / default</b>: when the LLM is unavailable, times out, or returns
/// non-JSON, the whole result falls back to a conservative draft (empty DisplayName + empty Name +
/// <see cref="FieldDataType.Text"/> + all false). The frontend treats empty DisplayName as "drafting
/// unavailable", preserves user-entered content, and prompts manual input.
/// </para>
/// </summary>
public class FieldDefinitionDraftDto
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Populated only when input <see cref="DraftFieldDefinitionInput.ForNewField"/>=true; always empty for edits.</summary>
    public string Name { get; set; } = string.Empty;

    public FieldDataType DataType { get; set; } = FieldDataType.Text;

    /// <summary>Guardrail 3: document semantics do not signal whether a field is required, so AI only returns conservative default false and admins decide.</summary>
    public bool IsRequired { get; set; }

    /// <summary>Guardrail 2: only <see cref="FieldDataType.Text"/> fields can be true, mirroring the entity invariant; non-text is always clamped to false by the server.</summary>
    public bool AllowMultiple { get; set; }
}
