using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dignite.Paperbase.Abstractions.Agents;

/// <summary>
/// Wraps an <see cref="AIAgent"/> with a validation-and-retry loop for structured
/// output extraction. Designed to be used together with
/// <see cref="AIAgentStructuredOutputExtensions.RunAsync{T}(AIAgent, string, AgentSession?, AgentRunOptions?, JsonSerializerOptions?, bool, CancellationToken)"/>:
///
/// <code>
/// var agent = new ChatClientAgent(chatClient, instructions: SystemPrompt)
///     .WithValidationRetry(validator, logger);
/// var run = await agent.RunAsync&lt;ContractExtractionResult&gt;(input);
/// </code>
///
/// On a failed validation the middleware appends a user-role feedback message
/// containing the rule violations (verbatim) and calls the inner agent again.
/// After <paramref name="maxRetries"/> failed attempts the final response is
/// returned unchanged — the caller's <c>RunAsync&lt;T&gt;</c> still deserializes
/// it but the business layer is expected to detect the low confidence (e.g. via
/// the validator's normalized confidence score) and route the record to manual
/// review rather than treating it as confirmed.
///
/// <para>
/// This is the official MAF cross-cutting concern surface (cf. the 1.5
/// <c>ClientHeadersAgent</c> in <c>Microsoft.Agents.AI</c> source) — agent-level
/// middleware, not <see cref="AIContextProvider"/>. Using
/// <see cref="AIContextProvider"/> here would inject RAG-style context into a
/// structured-extraction agent and is explicitly forbidden by
/// <c>.claude/rules/doc-chat-anti-patterns.md</c> § "反例 A".
/// </para>
/// </summary>
public static class StructuredExtractionRetryMiddleware
{
    /// <summary>
    /// Returns a new <see cref="AIAgent"/> that wraps <paramref name="agent"/> with a
    /// validate-and-retry loop driven by <paramref name="validator"/>. The validator's
    /// <see cref="ExtractionValidationResult.Errors"/> are sent back to the LLM as a
    /// user message — make them self-contained and actionable.
    /// </summary>
    /// <param name="agent">The inner agent (typically a <c>ChatClientAgent</c>).</param>
    /// <param name="validator">Domain-specific validator for <typeparamref name="T"/>.</param>
    /// <param name="logger">
    /// Used for warnings on each failed attempt. Pass <see cref="NullLogger.Instance"/>
    /// in tests; production callers should pass the caller's typed logger.
    /// </param>
    /// <param name="maxRetries">
    /// Maximum number of additional attempts after the initial one. Default 1
    /// (so at most 2 calls total). Keep low: each MAF <c>RunAsync</c> takes
    /// 8–15s with current models and an EventHandler that blocks much longer
    /// risks distributed-event backpressure.
    /// </param>
    /// <param name="serializerOptions">
    /// JSON options for deserializing the LLM payload. Defaults to
    /// <see cref="JsonSerializerDefaults.Web"/> which matches the casing
    /// convention used by <c>RunAsync&lt;T&gt;</c>'s built-in schema.
    /// </param>
    public static AIAgent WithValidationRetry<T>(
        this AIAgent agent,
        IExtractionValidator<T> validator,
        ILogger logger,
        int maxRetries = 1,
        JsonSerializerOptions? serializerOptions = null)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));
        if (validator is null) throw new ArgumentNullException(nameof(validator));
        if (logger is null) throw new ArgumentNullException(nameof(logger));
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));

        var options = serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);

        return agent.AsBuilder()
            .Use(runFunc: (messages, session, runOptions, innerAgent, ct) =>
                    RunWithValidationAsync(messages, session, runOptions, innerAgent, ct, validator, logger, maxRetries, options),
                 runStreamingFunc: null)
            .Build();
    }

    private static async Task<AgentResponse> RunWithValidationAsync<T>(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken,
        IExtractionValidator<T> validator,
        ILogger logger,
        int maxRetries,
        JsonSerializerOptions serializerOptions)
    {
        // Materialize once so feedback retries don't re-enumerate a lazy source.
        var baseMessages = new List<ChatMessage>(messages);
        AgentResponse? lastResponse = null;
        string? lastFeedback = null;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attemptMessages = lastFeedback is null
                ? (IEnumerable<ChatMessage>)baseMessages
                : BuildRetryMessages(baseMessages, lastFeedback);

            lastResponse = await innerAgent.RunAsync(attemptMessages, session, options, cancellationToken)
                .ConfigureAwait(false);

            var responseText = ExtractText(lastResponse);
            if (string.IsNullOrWhiteSpace(responseText))
            {
                lastFeedback = "Previous response had no textual content; produce a JSON object matching the requested schema.";
                logger.LogWarning("StructuredExtractionRetry attempt {Attempt} returned empty content.", attempt);
                continue;
            }

            T? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<T>(responseText, serializerOptions);
            }
            catch (JsonException ex)
            {
                lastFeedback = $"Previous response was not valid JSON ({ex.Message}); produce a JSON object matching the requested schema.";
                logger.LogWarning(ex, "StructuredExtractionRetry attempt {Attempt} failed to deserialize.", attempt);
                continue;
            }

            if (parsed is null)
            {
                lastFeedback = "Previous response deserialized to null; produce a non-null JSON object matching the requested schema.";
                logger.LogWarning("StructuredExtractionRetry attempt {Attempt} produced null after deserialization.", attempt);
                continue;
            }

            var validation = validator.Validate(parsed);
            if (validation.IsValid)
            {
                if (attempt > 0)
                {
                    logger.LogInformation("StructuredExtractionRetry recovered after {Attempt} retries.", attempt);
                }
                return lastResponse;
            }

            var errorMessages = string.Join("; ", validation.Errors.Select(e => e.Message));
            lastFeedback = "Previous extraction failed validation: " + errorMessages
                + ". Re-extract; preserve correct fields and only fix the invalid ones.";
            logger.LogWarning(
                "StructuredExtractionRetry attempt {Attempt} failed validation: {Errors}",
                attempt, errorMessages);
        }

        // Out of retries — return the last response. The caller's RunAsync<T> still
        // deserializes it; the business layer is responsible for routing low-confidence
        // results to manual review based on the validator's domain rules.
        logger.LogError("StructuredExtractionRetry exhausted {MaxRetries} retries without a valid result.", maxRetries);
        return lastResponse!;
    }

    private static IEnumerable<ChatMessage> BuildRetryMessages(
        IReadOnlyList<ChatMessage> baseMessages, string feedback)
    {
        foreach (var m in baseMessages)
        {
            yield return m;
        }
        yield return new ChatMessage(ChatRole.User, feedback);
    }

    private static string? ExtractText(AgentResponse response)
    {
        if (response.Messages is null || response.Messages.Count == 0)
        {
            return null;
        }

        // Most providers return a single assistant message with the JSON payload; concatenate
        // defensively in case a streaming-then-batched provider produces multiple text parts.
        var builder = new System.Text.StringBuilder();
        foreach (var m in response.Messages)
        {
            var text = m.Text;
            if (!string.IsNullOrEmpty(text))
            {
                builder.Append(text);
            }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }
}
