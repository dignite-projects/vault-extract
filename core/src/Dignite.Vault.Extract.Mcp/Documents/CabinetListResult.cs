using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Bounded return value for list_cabinets. Truncation is a safety boundary rather than pagination;
/// callers should use the returned cabinet names to clarify or narrow the requested scope.
/// </summary>
public sealed record CabinetListResult
{
    public required IReadOnlyList<CabinetSchema> Items { get; init; }

    public required int TotalCount { get; init; }

    public required bool Truncated { get; init; }
}
