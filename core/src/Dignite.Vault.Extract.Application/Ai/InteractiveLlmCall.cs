using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dignite.Vault.Extract.Ai;

/// <summary>
/// Shared "timeout + fail-open" envelope for interactive request/response LLM helpers (#264 review
/// #10).
/// <para>
/// Extract currently has two synchronous LLM drafting helpers: <c>SlugSuggestionAppService</c>
/// (DisplayName to slug) and <c>FieldDraftSuggestionAppService</c> (prompt to field metadata draft).
/// Their security control flow is identical: link the caller cancellation token, injected by ABP from
/// <c>HttpContext.RequestAborted</c>, with a server-side deadline; distinguish "client cancelled"
/// (rethrow as-is) from "server timeout / provider failure" (log warning + fallback); and avoid
/// letting LLM unavailability stall admin interactions. Centralizing this security-critical shell
/// prevents silent drift after duplication, such as changing deadline / cancellation-splitting
/// semantics in one helper but forgetting the other and weakening the declared fail-open guarantee.
/// </para>
/// <para>
/// Only the shell is encapsulated. Each call site still explicitly owns its prompt, ResponseFormat,
/// and parsing logic because .claude/rules/llm-call-anti-patterns.md requires every LLM entry point's
/// instructions / parsing to remain auditable instead of abstracted away.
/// </para>
/// </summary>
internal static class InteractiveLlmCall
{
    /// <summary>
    /// Calls <paramref name="chatClient"/> and returns the raw response text. Client cancellation is
    /// rethrown as-is; server timeout / other exceptions are logged as warnings and return
    /// <c>null</c>, letting callers fall back to their conservative defaults.
    /// </summary>
    public static async Task<string?> TryGetResponseTextAsync(
        IChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatResponseFormat responseFormat,
        TimeSpan timeout,
        ILogger logger,
        string callName,
        CancellationToken cancellationToken)
    {
        var options = new ChatOptions { ResponseFormat = responseFormat };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
            return response.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected / cancelled. This is normal, so rethrow to end the request by
            // cancellation semantics; do not count it as an LLM failure or create log noise.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Server deadline fired because the LLM was too slow or the provider did not observe
            // cancellation. Return null so the caller can fall back to a conservative default.
            logger.LogWarning(
                "{CallName} timed out after {TimeoutSeconds}s; returning null for fallback.",
                callName, (int)timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex)
        {
            // LLM unavailability must not stall admin interactions. Return null so the caller can
            // fall back to a conservative default.
            logger.LogWarning(ex, "{CallName} LLM call failed; returning null for fallback.", callName);
            return null;
        }
    }
}
