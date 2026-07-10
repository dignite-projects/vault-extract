using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Resolves caller-supplied <see cref="DocumentFieldFilter"/>s into internal <see cref="DocumentFieldQuery"/>
/// entries scoped to a single document type: each field name is looked up (#207: matched by the immutable
/// <c>FieldDefinitionId</c>) and its declared <c>DataType</c> attached, ready for
/// <see cref="IDocumentRepository.GetFieldMatchedIdsAsync"/>.
/// <para>
/// An unknown field name loud-fails with <see cref="VaultExtractErrorCodes.ExtractedField.Unknown"/> (a
/// correctable signal) instead of silently returning no rows. Shared by
/// <c>DocumentAppService.GetListAsync</c> (operator list + MCP search) and
/// <c>DocumentExportAppService.ExportAsync</c> (data download by field value), so this security-relevant
/// loud-fail resolution stays single-sourced and cannot drift between the list and export egress paths.
/// </para>
/// </summary>
public static class DocumentFieldQueryResolver
{
    /// <summary>
    /// Resolves <paramref name="filters"/> against the live field definitions of <paramref name="documentTypeId"/>.
    /// <para>
    /// <paramref name="knownDefinitions"/> (#501 item 4) is an optional cache of definitions the caller has
    /// <b>already</b> loaded for this same type — the export loads all of them to build its columns, and would
    /// otherwise re-fetch each filtered name from the database (<c>FindByNameAsync</c> is a
    /// <c>FirstOrDefaultAsync(predicate)</c>, which EF's identity map does not short-circuit; only <c>Find(key)</c>
    /// does). A cache <b>hit</b> skips the round-trip; a <b>miss</b> still asks the repository before failing.
    /// </para>
    /// <para>
    /// That fallback is the whole point, not defensive padding: the cache is matched with
    /// <see cref="StringComparison.Ordinal"/>, whereas <c>FindByNameAsync</c> compares in SQL, where the column's
    /// collation decides — SQL Server's default is case-<i>insensitive</i>. Loud-failing on an ordinal miss would
    /// therefore reject a name the database (and the list path, which has no cache) still accepts, quietly
    /// re-creating between the file and the screen the very divergence #501 item 1 removes. Only the database
    /// gets to say a field does not exist.
    /// </para>
    /// </summary>
    public static async Task<List<DocumentFieldQuery>> ResolveAsync(
        IFieldDefinitionRepository fieldDefinitionRepository,
        IReadOnlyList<DocumentFieldFilter> filters,
        Guid documentTypeId,
        string documentTypeCode,
        IReadOnlyList<FieldDefinition>? knownDefinitions = null)
    {
        var queries = new List<DocumentFieldQuery>(filters.Count);
        foreach (var filter in filters)
        {
            var definition =
                knownDefinitions?.FirstOrDefault(d => string.Equals(d.Name, filter.Name, StringComparison.Ordinal))
                ?? await fieldDefinitionRepository.FindByNameAsync(documentTypeId, filter.Name!);

            if (definition == null)
            {
                throw new BusinessException(VaultExtractErrorCodes.ExtractedField.Unknown)
                    .WithData("FieldName", filter.Name!)
                    .WithData("DocumentTypeCode", documentTypeCode);
            }

            // Internally match child rows by FieldDefinitionId (#207); FieldName is only for repository error diagnostics.
            queries.Add(new DocumentFieldQuery(
                definition.Id, filter.Name!, definition.DataType, filter.Value, filter.Min, filter.Max));
        }

        return queries;
    }
}
