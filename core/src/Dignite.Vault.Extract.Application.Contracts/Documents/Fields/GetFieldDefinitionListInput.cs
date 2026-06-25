using System;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>Field definition list query input for unified <c>GetListAsync</c>. Matches the current layer and never crosses layers.</summary>
public class GetFieldDefinitionListInput
{
    /// <summary>
    /// Target document type immutable Id (#207: internal associations use Id because TypeCode can be
    /// renamed and is not a reference key). Empty (<c>null</c>) means no type filter and returns all
    /// field definitions in the current layer in one call. This supports bulk readers such as MCP
    /// <c>list_document_types</c>, which fetch all definitions once and group by DocumentTypeId in
    /// memory, eliminating per-type N+1 queries. Permission gate is exactly the same as type-filtered
    /// queries, so visible scope is not widened; enumerating per type could already obtain the same
    /// set.
    /// </summary>
    public Guid? DocumentTypeId { get; set; }

    /// <summary>
    /// <c>true</c> returns only recycle-bin (soft-deleted) fields ordered by descending
    /// <c>DeletionTime</c>. <c>false</c> (default) returns active fields ordered by
    /// <c>DisplayOrder</c>, or by <c>DocumentTypeId</c> first in bulk mode. The two views are mutually
    /// exclusive.
    /// </summary>
    public bool OnlyDeleted { get; set; }
}
