using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dignite.Vault.Extract.Slugging;

/// <summary>
/// Suggests a machine identifier (slug) from a display name (issue #190). Shared by FieldDefinition
/// and DocumentType creation forms.
///
/// <para>
/// This is Extract's first synchronous request/response LLM call site; the other LLM calls are in
/// BackgroundJob / EventHandler flows. It aligns with the security covenant (CLAUDE.md "Security
/// Covenant" / .claude/rules/llm-call-anti-patterns.md) point by point:
/// </para>
/// <list type="number">
///   <item><b>Fail-closed authorization</b>: class-level <c>[Authorize(ConfirmClassification)]</c>.
///         This is a real HTTP AppService exposed through SlugSuggestionController, so the attribute
///         is enforced at the HTTP boundary, matching FieldDefinitionAppService /
///         DocumentTypeAppService.</item>
///   <item><b>No DB query</b>: plain text in, text out. It touches no <c>IRepository</c> / raw SQL, so
///         Take(N) and explicit TenantId predicates do not apply.</item>
///   <item><b>PromptBoundary</b>: the user-derived free-text Label is wrapped with
///         <see cref="PromptBoundary.WrapField"/> before entering the prompt, and
///         <see cref="PromptBoundary.BoundaryRule"/> is appended.</item>
///   <item><b>Compile-time constant instructions</b>: <see cref="SlugSystemPrompt"/> is <c>const</c>
///         and concatenates no runtime strings.</item>
///   <item><b>Do not trust LLM output</b>: the result is constrained to <c>[a-z0-9_]</c> through
///         <see cref="SlugNormalizer.Sanitize"/> and is only an admin-editable suggestion. The final
///         Create path still goes through FieldDefinition/DocumentType whitelist validation.</item>
/// </list>
/// </summary>
[Authorize(ExtractPermissions.Documents.ConfirmClassification)]
public class SlugSuggestionAppService : ExtractAppService, ISlugSuggestionAppService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<SlugSuggestionAppService> _logger;

    public SlugSuggestionAppService(
        [FromKeyedServices(ExtractConsts.StructuredChatClientKey)] IChatClient chatClient,
        ILogger<SlugSuggestionAppService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// Server-side hard timeout. The frontend 8s timeout only protects the browser side: non-Angular
    /// callers, clients that keep the connection open, or providers that do not promptly observe
    /// request-abort could still tie up request handling and token quota. As the first interactive
    /// request/response LLM path, the backend needs its own deadline fallback, set slightly above the
    /// frontend 8s timeout.
    /// </summary>
    private static readonly TimeSpan SuggestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Compile-time constant system instructions. Runtime string concatenation is not allowed, to
    /// prevent prompt injection.
    /// </summary>
    private const string SlugSystemPrompt =
        "You convert a human-readable label into a short machine identifier (a \"slug\"). " +
        "Translate non-English labels into concise English first, then form the slug. " +
        "Output rules: lowercase ASCII snake_case using only letters a-z, digits 0-9 and single " +
        "underscores between words; 1 to 3 words; at most 64 characters; no leading or trailing " +
        "underscore; no spaces; no punctuation other than underscores; no quotes. " +
        "Examples: \"Contract Amount\" -> \"contract_amount\"; \"Party Name\" -> \"party_name\"; \"Issue Date\" -> \"issue_date\". " +
        "Return JSON only in the form {\"slug\": \"...\"}.";

    public virtual async Task<SlugSuggestionDto> SuggestAsync(
        SuggestSlugInput input,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SlugSystemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
            // Label is user-derived free text and must be explicitly marked as data through
            // PromptBoundary.WrapField.
            new(ChatRole.User, "Label:\n" + PromptBoundary.WrapField(input.Label))
        };

        // Timeout + fail-open shell is centralized in InteractiveLlmCall, shared with
        // FieldDraftSuggestionAppService (#264 review #10). Client cancellation is rethrown as-is;
        // server timeout / provider failures return null, and ExtractSlug(null) falls back to an
        // empty slug so the frontend can use its local placeholder.
        var rawJson = await InteractiveLlmCall.TryGetResponseTextAsync(
            _chatClient, messages, SlugResponseFormat, SuggestTimeout, _logger, "Slug suggestion", cancellationToken);

        return new SlugSuggestionDto { Slug = ExtractSlug(rawJson) };
    }

    /// <summary>
    /// Extracts and sanitizes the <c>slug</c> field from the LLM JSON output. Any parse failure returns
    /// an empty string, letting the frontend fallback take over.
    /// </summary>
    protected virtual string ExtractSlug(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("slug", out var slugProp) &&
                slugProp.ValueKind == JsonValueKind.String)
            {
                return SlugNormalizer.Sanitize(slugProp.GetString());
            }

            // Valid JSON but schema drift (missing slug key / non-string). Fallback still works, but
            // log once so model behavior can be analyzed offline.
            _logger.LogWarning("Slug suggestion JSON missing a string 'slug' property: {Raw}", rawJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Slug suggestion returned non-JSON output: {Raw}", rawJson);
        }

        return string.Empty;
    }

    private static readonly ChatResponseFormat SlugResponseFormat = CreateSlugResponseFormat();

    private static ChatResponseFormat CreateSlugResponseFormat()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["slug"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = @"^[a-z0-9_]{1,64}$",
                    ["description"] = "A lowercase ASCII snake_case slug."
                }
            },
            ["required"] = new JsonArray("slug"),
            ["additionalProperties"] = false
        };

        using var document = JsonDocument.Parse(schema.ToJsonString());
        return ChatResponseFormat.ForJsonSchema(
            document.RootElement.Clone(),
            schemaName: "ExtractSlugSuggestion",
            schemaDescription: "A single suggested Extract machine identifier.");
    }
}
