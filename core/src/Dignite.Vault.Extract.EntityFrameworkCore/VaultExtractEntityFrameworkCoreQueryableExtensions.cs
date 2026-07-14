using System.Linq;
using Dignite.Vault.Extract.Documents;
using Microsoft.EntityFrameworkCore;

namespace Dignite.Vault.Extract;

public static class VaultExtractEntityFrameworkCoreQueryableExtensions
{
    public static IQueryable<Document> IncludeDetails(
        this IQueryable<Document> queryable,
        bool include = true)
    {
        if (!include)
        {
            return queryable;
        }

        // This generic loader eager-loads only ExtractedFieldValues. The #527 FieldValidationWarnings child is
        // deliberately NOT co-loaded here: IncludeDetails feeds the list / generic read paths, where a second collection
        // Include would reintroduce the #206 Cartesian product across many documents. Warnings are loaded only where the
        // aggregate reconciles / clears them, by FindWithFieldValuesAsync (single-document, AsSplitQuery); the
        // review-queue list projects a bounded warning summary instead of hydrating the collection. So a single Include
        // here still has no Cartesian-product risk and needs no AsSplitQuery.
        // PipelineRuns are no longer eager-loaded here since #216 split them into an independent aggregate
        // root; queries go through IDocumentPipelineRunRepository.
        return queryable.Include(x => x.ExtractedFieldValues);
    }
}
