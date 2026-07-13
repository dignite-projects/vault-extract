using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Document type field schema: the LLM-facing read projection for MCP resource
/// a document-type resource URI, ambient or explicitly tenant-scoped. It lets downstream AI clients discover which fields a type
/// has and what data types they use, so they can populate the search tool's <c>fieldFilters</c> /
/// <c>includeFields</c> with correct field names. <see cref="DisplayName"/> is admin-configured
/// user-derived text and is already wrapped with <c>PromptBoundary.WrapField</c> to prevent indirect
/// prompt injection. <see cref="TypeCode"/> / field <c>Name</c> / <c>DataType</c> are
/// system-controlled values (whitelist / enum), so they are emitted raw.
/// </summary>
public sealed record DocumentTypeSchema
{
    public required string TypeCode { get; init; }

    /// <summary>Resource URI for this schema. Uses an explicit tenant scope when the caller supplied tenantId.</summary>
    public required string Uri { get; init; }

    /// <summary>Type display name, already wrapped with PromptBoundary.</summary>
    public string? DisplayName { get; init; }

    public required IReadOnlyList<DocumentTypeFieldSchema> Fields { get; init; }
}

/// <summary>
/// Schema projection for a single field. <see cref="Name"/> is the immutable identifier used in
/// <c>fieldFilters</c> / <c>includeFields</c>. <see cref="DataType"/> determines available query
/// operators: Text / Boolean support equality only, while numbers / dates support ranges.
/// <see cref="AllowMultiple"/> tells clients whether the field appears in search-result
/// <c>extractedFields</c> as an array or scalar (#212). <see cref="DisplayName"/> is already wrapped
/// with PromptBoundary. Extraction instruction <c>Prompt</c> is intentionally omitted because it is
/// useless for query / projection orchestration and would waste LLM context while increasing the
/// injection surface.
/// </summary>
public sealed record DocumentTypeFieldSchema
{
    public required string Name { get; init; }

    /// <summary>Field data type (<c>FieldDataType</c> enum name: Text / Number / Boolean / Date / DateTime / LongText).</summary>
    public required string DataType { get; init; }

    /// <summary>
    /// Whether the field is multi-value (#212, true only for text fields). When true, the field is a
    /// <b>JSON array</b> (<c>string[]</c>) in search-result <c>extractedFields</c> rather than a scalar
    /// string, so clients can parse it correctly. Equality filtering still matches one value and
    /// returns documents containing that value.
    /// </summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Field display name, already wrapped with PromptBoundary.</summary>
    public string? DisplayName { get; init; }

    public bool IsRequired { get; init; }
}
