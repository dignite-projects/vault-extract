using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Structured return value for the <c>list_document_types</c> tool (LLM-facing).
/// <see cref="Types"/> is stably sorted by TypeCode and truncated to
/// <see cref="ExtractMcpConsts.MaxDocumentTypeResults"/>, a hard result cap from
/// llm-call-anti-patterns counterexample B point 3. When over the limit, <see cref="Truncated"/> +
/// <see cref="TotalCount"/> explicitly tell the LLM more exist. Truncation is a safety boundary, not
/// pagination, so this tool provides no paging parameters.
/// </summary>
public sealed record DocumentTypeListResult
{
    /// <summary>Document type field schemas visible to the current principal, ordered by TypeCode ascending and capped to MaxDocumentTypeResults.</summary>
    public required IReadOnlyList<DocumentTypeSchema> Types { get; init; }

    /// <summary>Total number of types visible to the current principal, including those omitted by the cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary><c>true</c> means visible types exceed the per-call cap and <see cref="Types"/> contains only the lexicographically first TypeCode segment.</summary>
    public required bool Truncated { get; init; }
}
