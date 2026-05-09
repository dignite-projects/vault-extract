namespace Dignite.Paperbase.Ai;

/// <summary>
/// Public constants used by the host wiring (<c>PaperbaseHostModule.ConfigureAI</c>) and
/// the application layer (services consuming keyed AI clients via DI).
/// </summary>
public static class PaperbaseAIConsts
{
    /// <summary>
    /// DI key for the summarizer <see cref="Microsoft.Extensions.AI.IChatClient"/>
    /// used by <c>SummarizationCompactionStrategy</c>. Host registers under this key;
    /// the application layer pulls via <c>[FromKeyedServices(...)]</c>.
    /// Hosts that don't configure a separate summarizer model fall back to the same
    /// underlying chat model — the application layer must accept that arrangement.
    /// </summary>
    public const string SummarizerChatClientKey = "paperbase-summarizer";
}
