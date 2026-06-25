namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Workflow system prompt template returned by <see cref="IPromptProvider"/>.
/// </summary>
/// <param name="SystemInstructions">
/// Main system instruction text, without PromptBoundary rules. Workflows append those at the use site.
/// </param>
public record PromptTemplate(string SystemInstructions);
