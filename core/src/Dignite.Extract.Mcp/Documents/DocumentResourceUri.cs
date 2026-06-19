using System;

namespace Dignite.Extract.Mcp.Documents;

/// <summary>
/// Single source for MCP document resource URIs. Resource templates for the read path and rows returned
/// by search tools share the same scheme, preventing hand-written <c>extract://documents/...</c> values
/// from drifting across locations and breaking read-after-search.
/// </summary>
public static class DocumentResourceUri
{
    private const string Prefix = "extract://documents/";

    /// <summary>Resource URI template. Used by <c>[McpServerResource(UriTemplate = ...)]</c> and must be a compile-time constant.</summary>
    public const string Template = Prefix + "{id}";

    public static string Format(Guid documentId) => Prefix + documentId;
}
