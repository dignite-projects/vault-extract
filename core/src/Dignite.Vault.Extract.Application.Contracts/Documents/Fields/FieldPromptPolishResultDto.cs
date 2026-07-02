namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Result of AI-polishing a field prompt (#447): the improved Markdown, written back into the prompt editor
/// for the administrator to review before saving. On LLM unavailability / timeout the server returns the
/// original prompt unchanged (fail-open), so the button is a no-op rather than destroying the input.
/// </summary>
public class FieldPromptPolishResultDto
{
    public string Prompt { get; set; } = default!;
}
