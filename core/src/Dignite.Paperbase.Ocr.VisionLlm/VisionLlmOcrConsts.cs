namespace Dignite.Paperbase.Ocr.VisionLlm;

/// <summary>
/// Constants for the vision-LLM OCR provider.
/// </summary>
public static class VisionLlmOcrConsts
{
    /// <summary>
    /// DI key for the vision-capable <c>IChatClient</c> the VisionLlm OCR provider resolves.
    /// <para>
    /// Registered by the host wiring (<c>PaperbaseHostModule.ConfigureAI</c>) and consumed by
    /// <see cref="VisionLlmOcrProvider"/> via <c>[FromKeyedServices(...)]</c> — the same keyed-client
    /// pattern as <c>PaperbaseAIConsts.StructuredChatClientKey</c> / <c>TitleGeneratorChatClientKey</c>.
    /// </para>
    /// <para>
    /// The key lives here (not in <c>PaperbaseAIConsts</c>, which is in the Application layer) on purpose:
    /// an OCR provider sits at the bottom of the dependency graph and must not reference the orchestration
    /// layer. Both the host registration and this provider reference this same constant, so the key string
    /// can never drift between the two sides.
    /// </para>
    /// </summary>
    public const string VisionChatClientKey = "paperbase-vision-ocr";
}
