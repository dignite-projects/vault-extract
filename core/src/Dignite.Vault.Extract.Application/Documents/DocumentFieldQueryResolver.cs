using System;
using System.Collections.Generic;
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
    public static async Task<List<DocumentFieldQuery>> ResolveAsync(
        IFieldDefinitionRepository fieldDefinitionRepository,
        IReadOnlyList<DocumentFieldFilter> filters,
        Guid documentTypeId,
        string documentTypeCode)
    {
        var queries = new List<DocumentFieldQuery>(filters.Count);
        foreach (var filter in filters)
        {
            var definition = await fieldDefinitionRepository.FindByNameAsync(documentTypeId, filter.Name!);
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
