using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// MCP search tool: a thin shell that assembles LLM input into <see cref="GetDocumentListInput"/> and delegates to
/// <see cref="IDocumentAppService.GetListAsync"/>. It shares the same use-case entry point as the REST list endpoint:
/// permission assertions, parameter validation, field definition resolution, and tenant isolation all run inside AppService.
/// Retrieval converges to structured field queries: metadata plus one or more ExtractedFields value filters, ANDed together.
/// There is no keyword full-text / substring search because degraded retrieval engines are OUT of scope; see CLAUDE.md / Issue #204.
/// Field value queries are anchored to required <c>documentTypeCode</c>: fields such as amount have no stable meaning outside a type.
/// Metadata-only queries may instead be anchored to a cabinet or source container.
/// Clarification happens in the client LLM; Extract does not implement a conversational agent.
/// Matching rows include all ExtractedFields for the document directly, avoiding a second fetch. This tool handles only transport-layer concerns:
/// lenient parsing of lifecycle strings, hard result-set limit clamped to <see cref="DocumentConsts.MaxSearchResultCount"/> to protect LLM context,
/// wrapping title / field values with <c>PromptBoundary</c> to prevent indirect prompt injection, and surfacing a
/// truncation signal (<see cref="DocumentSearchResult.Truncated"/> / <see cref="DocumentSearchResult.TotalCount"/>)
/// so a capped result is not mistaken for the complete set (#445, parity with list_document_types).
/// </summary>
[McpServerToolType]
public sealed class DocumentSearchTool
{
    [McpServerTool(Name = "search_documents", Title = "Search Documents", ReadOnly = true)]
    [Description("Search Extract documents within a required structured scope by metadata and/or one or "
        + "more extracted-field filters (all combined with AND). Returns an object with items "
        + "(up to 50: id, uri, cabinet id, title, type, lifecycle, created-at, and extracted field values) "
        + "plus totalCount and truncated; when truncated is true more documents matched than were returned, so "
        + "narrow the query rather than treating items as the complete set. When an item's fieldExtractionDeclined is "
        + "true its document was too large to extract fields from, so that item's extracted field values are empty or "
        + "out of date and must not be treated as current. Read a match's full Markdown via its "
        + "resource uri (tenant-scoped when tenantId is supplied). Structured field/metadata "
        + "search only — no keyword/full-text or semantic/vector retrieval. At least one scope anchor is required: "
        + "documentTypeCode, originDocumentId, or cabinetId. Extracted-field filters always require documentTypeCode. "
        + "To list the sub-documents of a container, pass that container's id as originDocumentId. Discover a "
        + "type's filterable fields via a document-type resource uri; discover cabinet ids via "
        + "resources/list or list_cabinets.")]
    public static async Task<DocumentSearchResult> SearchAsync(
        IDocumentAppService documentAppService,
        [Description("The document type code to search within (e.g. a classification result like "
            + "'contract.general'). Required UNLESS originDocumentId is given; a field value query always needs "
            + "it to resolve each field's data type. If unknown and not listing a container's children, ask the "
            + "user which document type to search.")]
        string? documentTypeCode = null,
        [Description("List only the sub-documents derived from this source document id — i.e. the children of a "
            + "container (the documents whose origin is this id). Pass a container's id here to enumerate its "
            + "sub-documents. Optional; when given, documentTypeCode is not required.")]
        string? originDocumentId = null,
        [Description("Filter to one cabinet id (UUID). Resolve a user-facing cabinet name through resources/list "
            + "or list_cabinets first. Optional; when given, documentTypeCode is not required unless fieldFilters "
            + "are also supplied.")]
        string? cabinetId = null,
        [Description("Filter by lifecycle status. One of: Uploaded, Processing, Ready, Failed, Archived. Optional.")]
        string? lifecycleStatus = null,
        [Description("Extracted-field filters, all combined with AND (every filter must match). Each entry "
            + "names a field defined on the document type plus either an exact Value or an inclusive numeric/date "
            + "Min/Max range. Each field's data type is resolved server-side. Omit for a metadata-only search. Optional.")]
        // Must be concrete List<T>. Do not change to IReadOnlyList<T> / IEnumerable<T> / ICollection<T> / array.
        // ABP uses the Autofac container, which treats all collection relationship types as implicitly resolvable DI services
        // (IServiceProviderIsService.IsService returns true). The MCP SDK parameter binder then removes that parameter
        // from the inputSchema visible to the LLM (ExcludeFromSchema), so the LLM never sees it and field filtering silently stops working.
        // Guarded by DocumentSearchTool_Tests schema tests + counterexample C in .claude/rules/llm-call-anti-patterns.md.
        List<DocumentFieldFilter>? fieldFilters = null,
        [Description("Max rows to return (1-50). Defaults to 50.")]
        int? maxResultCount = null,
        [Description("Optional tenant id (UUID). When supplied, search only that tenant and returned resource URIs retain this tenant scope.")]
        string? tenantId = null,
        CancellationToken cancellationToken = default,
        IServiceProvider? serviceProvider = null)
    {
        var explicitTenantId = McpTenantScope.Parse(tenantId);
        using var tenantScope = McpTenantScope.Change(explicitTenantId, serviceProvider);
        // Sub-document provenance query (#354): listing a container's children is anchored by originDocumentId,
        // not by type — the children are heterogeneously typed and their types are unknown to the caller. Parse
        // leniently (LLM clients pass string GUIDs); a malformed value is treated as "no provenance filter".
        Guid? originDocumentIdValue = null;
        if (!string.IsNullOrWhiteSpace(originDocumentId)
            && Guid.TryParse(originDocumentId, out var parsedOriginId))
        {
            originDocumentIdValue = parsedOriginId;
        }

        // CabinetId is a query-scope anchor, so parse it strictly. This intentionally differs from the
        // legacy lenient origin/lifecycle parsing above: silently dropping an invalid cabinet id could
        // broaden a request across every cabinet, or produce a misleading missing-type error.
        Guid? cabinetIdValue = null;
        if (!string.IsNullOrWhiteSpace(cabinetId))
        {
            if (!Guid.TryParse(cabinetId, out var parsedCabinetId))
            {
                throw new McpException($"Invalid cabinet id: {cabinetId}");
            }
            cabinetIdValue = parsedCabinetId;
        }

        // Extracted field declarations are type-bound even when the metadata search is cabinet-scoped.
        if (fieldFilters is { Count: > 0 } && string.IsNullOrWhiteSpace(documentTypeCode))
        {
            throw new McpException("documentTypeCode is required when fieldFilters are specified.");
        }

        // A type, provenance id, or cabinet id is required so a malformed/underspecified request cannot
        // degrade to retrieval across the entire current layer.
        if (string.IsNullOrWhiteSpace(documentTypeCode)
            && originDocumentIdValue is null
            && cabinetIdValue is null)
        {
            throw new McpException(
                "At least one search scope is required: documentTypeCode, originDocumentId, or cabinetId. "
                + "Extracted-field filters require documentTypeCode. Discover document types through "
                + "list_document_types and cabinet ids through list_cabinets.");
        }

        // Leniently parse lifecycle filter values. LLM clients usually pass string names such as "Ready".
        // If parsing fails, treat it as "no filter". Missing filter only returns more results and is still bounded by result limits;
        // permissions / tenant / limits remain enforced inside AppService.
        DocumentLifecycleStatus? lifecycle = null;
        if (!string.IsNullOrWhiteSpace(lifecycleStatus)
            && Enum.TryParse<DocumentLifecycleStatus>(lifecycleStatus, ignoreCase: true, out var parsedLifecycle))
        {
            lifecycle = parsedLifecycle;
        }

        var input = new GetDocumentListInput
        {
            DocumentTypeCode = documentTypeCode,
            OriginDocumentId = originDocumentIdValue,
            CabinetId = cabinetIdValue,
            LifecycleStatus = lifecycle,
            FieldFilters = fieldFilters?.ToList(),
            // Clamp hard result-set limit to MaxSearchResultCount. This is an MCP transport concern:
            // protect LLM context and prevent prompt injection from inducing broad queries. REST lists use normal pagination and are not constrained by this.
            MaxResultCount = Math.Clamp(
                maxResultCount ?? DocumentConsts.MaxSearchResultCount, 1, DocumentConsts.MaxSearchResultCount),
            SkipCount = 0
        };

        // Delegate to the AppService use case: permission assertion (CheckPolicyAsync), DTO validation, field definition resolution,
        // tenant isolation, and field value filtering all execute there consistently. MCP dispatch does not pass through HTTP [Authorize],
        // but CheckPolicyAsync inside the AppService method body is the real enforcement gate and still applies.
        // This is the permission defense for LLM paths.
        var result = await documentAppService.GetListAsync(input);

        var items = result.Items
            .Select(d => new DocumentSearchResultItem
            {
                Uri = DocumentResourceUri.Format(d.Id, explicitTenantId),
                Id = d.Id,
                CabinetId = d.CabinetId,
                // User-derived free text (title) is wrapped with PromptBoundary to prevent indirect prompt injection.
                Title = PromptBoundary.WrapField(d.Title),
                DocumentTypeCode = d.DocumentTypeCode,
                LifecycleStatus = d.LifecycleStatus.ToString(),
                CreationTime = d.CreationTime,
                // Container / sub-document provenance (#350). System-controlled signals, not user free text,
                // so no PromptBoundary wrapping. Sub-documents are not inlined here to keep the payload thin;
                // a client reads them by searching OriginDocumentId == this Id.
                IsContainer = d.IsContainer,
                OriginDocumentId = d.OriginDocumentId,
                // Convert all extracted field values for this document to LLM-facing shape: strings wrapped, structured values raw, null skipped.
                ExtractedFields = DocumentFieldProjection.Project(d.ExtractedFields),
                // #491: this row carries ExtractedFields, and a declined document's are empty or stale (the gate
                // preserves values from an earlier in-budget run rather than deleting them). Flag it, or an LLM reads
                // them as current. A system-controlled bool, so no PromptBoundary wrapping.
                FieldExtractionDeclined =
                    (d.ReviewReasons & DocumentReviewReasons.FieldExtractionIncomplete) != DocumentReviewReasons.None
            })
            .ToList();

        // Parity with list_document_types (DocumentTypeListResult): the hard cap can elide matches, so carry
        // an explicit truncation signal. Without it the calling LLM cannot tell a complete result from "the
        // first N of thousands" and may answer as if it had seen every match. TotalCount is the pre-cap match
        // count from the paged use case; Truncated trips whenever more matched than were returned (the
        // caller's maxResultCount, or the MaxSearchResultCount ceiling).
        return new DocumentSearchResult
        {
            Items = items,
            TotalCount = (int)result.TotalCount,
            Truncated = result.TotalCount > items.Count
        };
    }
}
