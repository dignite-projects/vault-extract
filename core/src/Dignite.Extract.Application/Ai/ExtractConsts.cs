namespace Dignite.Extract.Ai;

/// <summary>
/// DI keys for the keyed <c>IChatClient</c> instances Extract's core LLM call sites resolve.
/// Registered by the host wiring (<c>ExtractHostModule.ConfigureAI</c>) and consumed by the
/// classification / field-extraction / slug-suggestion / title-generation paths.
/// See <c>docs/ai-provider.md</c> for the keyed-clients table and what each is for.
/// </summary>
public static class ExtractConsts
{
    /// <summary>
    /// DI key for the document-title-generator <c>IChatClient</c>
    /// used by <c>DocumentParseBackgroundJob.TryGenerateTitleAsync</c>. This is a
    /// single-shot text-completion path: no tools, no distributed cache (each prompt is
    /// unique), no FunctionInvocation wrapper. Splitting it off from the main chat client
    /// keeps trace structure honest (no phantom <c>orchestrate_tools</c> spans around a
    /// tool-free call) and lets hosts pick a cheaper / faster model for the title side.
    /// </summary>
    public const string TitleGeneratorChatClientKey = "extract-title-generator";

    /// <summary>
    /// DI key for the structured-output <c>IChatClient</c>
    /// shared by all single-shot, tool-free, prompt-unique structured-output call sites:
    /// <c>DocumentClassificationWorkflow</c>, the field-extraction pipeline, and slug suggestions.
    ///
    /// <para>
    /// Same shape as the title-generator client (no FunctionInvocation, no DistributedCache) —
    /// these calls do not invoke tools, their prompts are document-content-derived or
    /// admin-input-derived (unique per call), and their outputs are schema-bound by
    /// MAF <c>RunAsync&lt;T&gt;</c> or <c>ChatResponseFormat.ForJsonSchema</c>. Wrapping them with
    /// FunctionInvocation just produces phantom <c>orchestrate_tools</c> spans on traces,
    /// and DistributedCache lookups always miss.
    /// </para>
    ///
    /// <para>
    /// Hosts that want per-task model tuning (e.g. small fast model for classification,
    /// stronger model for field extraction) can override <c>ConfigureAI</c> to register
    /// additional per-purpose keyed clients on top of this default consolidation.
    /// </para>
    /// </summary>
    public const string StructuredChatClientKey = "extract-structured";
}
