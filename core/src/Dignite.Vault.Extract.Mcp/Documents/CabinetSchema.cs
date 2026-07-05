using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// LLM-facing cabinet projection shared by the cabinet resource and list_cabinets tool. Name and
/// Description are administrator-controlled free text and are PromptBoundary-wrapped before use.
/// </summary>
public sealed record CabinetSchema
{
    public required Guid Id { get; init; }

    public required string Uri { get; init; }

    /// <summary>Cabinet display name, already wrapped with PromptBoundary.WrapField.</summary>
    public required string Name { get; init; }

    /// <summary>Optional cabinet description, already wrapped with PromptBoundary.WrapField.</summary>
    public string? Description { get; init; }
}
