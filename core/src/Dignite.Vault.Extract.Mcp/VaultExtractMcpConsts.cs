namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Transport-layer constants for the MCP outbound adapter. Same discipline as
/// <c>DocumentConsts.MaxSearchResultCount</c> (.claude/rules/llm-call-anti-patterns.md
/// counterexample B point 3): hard result-set caps on LLM-triggered paths are always compile-time
/// <c>const</c> values, so the safety boundary cannot be widened by runtime configuration.
/// </summary>
public static class VaultExtractMcpConsts
{
    /// <summary>
    /// Root of the MCP resource URI scheme. Single source every per-resource URI helper derives from,
    /// so the scheme cannot drift across resource kinds.
    /// </summary>
    public const string UriScheme = "vault-extract://";

    /// <summary>
    /// Root for explicit-tenant resource URIs (<c>vault-extract://tenants/{tenantId}/...</c>). Shared by
    /// every per-resource URI helper so the tenant URI shape cannot drift per resource kind, and so a
    /// helper's <c>Format</c> output and its registered resource template stay in lockstep.
    /// </summary>
    public const string TenantUriRoot = UriScheme + "tenants/";

    /// <summary>
    /// Hard cap on the number of document types returned in one document type enumeration
    /// (<c>list_document_types</c> tool and <c>resources/list</c>). Tenant admins can create any number
    /// of document types; unbounded enumeration can blow up LLM context and create a cost-attack
    /// surface. 100 is twice <c>DocumentConsts.MaxSearchResultCount</c>: types are schema-level
    /// metadata, each item is much smaller than a document search row because there is no Markdown /
    /// field-value payload, and normal deployments have at most dozens of types. 100 covers normal
    /// discovery while keeping pathological scale (thousands of types) outside the boundary.
    /// Truncation happens after stable TypeCode ordering; tool outbound payloads explicitly tell the
    /// LLM more exist via <c>truncated</c> + <c>totalCount</c>.
    /// </summary>
    public const int MaxDocumentTypeResults = 100;

    /// <summary>
    /// Hard cap on the number of Markdown characters of a single document body handed to an MCP client
    /// (<c>get_document</c> tool and the <c>vault-extract://documents/{id}</c> resource, including its
    /// explicit-tenant equivalent). The existing
    /// <c>Take(N)</c> discipline bounds the <b>row count</b> of a result set but says nothing about the
    /// <b>payload size of one row</b>, so a single read of a large document could consume the client's
    /// entire context window — the exact harm .claude/rules/llm-call-anti-patterns.md counterexample B
    /// point 3 exists to prevent (#491). 200k characters is roughly 50-70k tokens: large enough that
    /// ordinary documents are never truncated, small enough to leave a modern client room to reason.
    /// Truncation happens at a UTF-16 char boundary and is always announced to the LLM — the tool via
    /// <c>markdownTruncated</c> + <c>markdownTotalChars</c>, the resource via its metadata header — so a
    /// silently clipped body can never be mistaken for a complete one.
    /// </summary>
    public const int MaxDocumentMarkdownChars = 200_000;
}
