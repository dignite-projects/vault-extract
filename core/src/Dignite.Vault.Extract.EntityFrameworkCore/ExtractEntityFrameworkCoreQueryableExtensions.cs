using System.Linq;
using Dignite.Vault.Extract.Documents;
using Microsoft.EntityFrameworkCore;

namespace Dignite.Vault.Extract;

public static class ExtractEntityFrameworkCoreQueryableExtensions
{
    public static IQueryable<Document> IncludeDetails(
        this IQueryable<Document> queryable,
        bool include = true)
    {
        if (!include)
        {
            return queryable;
        }

        // Only one collection child remains, ExtractedFieldValues (#206). A single Include has no
        // Cartesian-product risk, so AsSplitQuery is no longer needed.
        // PipelineRuns are no longer eager-loaded here since #216 split them into an independent aggregate
        // root; queries go through IDocumentPipelineRunRepository.
        return queryable.Include(x => x.ExtractedFieldValues);
    }
}
