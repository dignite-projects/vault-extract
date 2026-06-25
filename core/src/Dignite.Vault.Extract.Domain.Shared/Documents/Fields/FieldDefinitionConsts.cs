namespace Dignite.Vault.Extract.Documents.Fields;

public static class FieldDefinitionConsts
{
    public static int MaxNameLength { get; set; } = 64;
    public static int MaxDisplayNameLength { get; set; } = 128;
    public static int MaxPromptLength { get; set; } = 1024;

    /// <summary>
    /// Whitelist for field <see cref="FieldDefinition.Name"/>: only letters / digits / underscore /
    /// hyphen, 1-64 characters. Prompt injection defense: Name is concatenated literally into the JSON
    /// schema description in the LLM prompt, so newlines / punctuation / Markdown control characters
    /// must not enter prompt context.
    /// <para>
    /// Must be <c>const</c>: this is a safety boundary in the LLM prompt injection defense chain; see
    /// <see cref="FieldDefinition"/> XML docs and related comments in <c>FieldExtractionWorkflow</c>.
    /// Any runtime mutation would pierce the whitelist and add an attack surface. Also,
    /// <c>FieldDefinition</c> caches this field once into a static readonly Regex when the type loads,
    /// so runtime overrides would not take effect and would become a footgun.
    /// </para>
    /// </summary>
    public const string NamePattern = @"^[A-Za-z0-9_\-]{1,64}$";
}
