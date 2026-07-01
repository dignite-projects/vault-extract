using System.Collections.Generic;
using Dignite.Vault.Extract.Documents;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Structured return value for the <c>search_documents</c> tool (LLM-facing). <see cref="Items"/> is
/// hard-capped to <see cref="DocumentConsts.MaxSearchResultCount"/> — a fail-closed boundary against
/// prompt-injection-induced broad queries / LLM-context blowup (llm-call-anti-patterns counterexample B
/// point 3), <b>not</b> pagination, so this tool provides no paging parameters. When more documents matched
/// than were returned, <see cref="Truncated"/> + <see cref="TotalCount"/> explicitly tell the LLM the result
/// is partial, so it does not answer as if it had seen every match. Mirrors <see cref="DocumentTypeListResult"/>
/// (#445).
/// </summary>
public sealed record DocumentSearchResult
{
    /// <summary>The matching documents, capped to the per-call limit and ordered as the list use case returns them.</summary>
    public required IReadOnlyList<DocumentSearchResultItem> Items { get; init; }

    /// <summary>Total number of documents matching the query, including those omitted by the cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary><c>true</c> when more documents matched than were returned (the cap elided some); the caller has NOT seen the complete result set.</summary>
    public required bool Truncated { get; init; }
}
