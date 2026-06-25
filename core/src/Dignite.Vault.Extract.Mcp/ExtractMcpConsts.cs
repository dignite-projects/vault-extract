namespace Dignite.Vault.Extract.Mcp;

/// <summary>
/// Transport-layer constants for the MCP outbound adapter. Same discipline as
/// <c>DocumentConsts.MaxSearchResultCount</c> (.claude/rules/llm-call-anti-patterns.md
/// counterexample B point 3): hard result-set caps on LLM-triggered paths are always compile-time
/// <c>const</c> values, so the safety boundary cannot be widened by runtime configuration.
/// </summary>
public static class ExtractMcpConsts
{
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
}
