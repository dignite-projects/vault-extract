using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// Narrow read of a visible document type for internal callers that only need identity and display text —
/// today the three in-process MCP adapters (<c>DocumentTypeTools</c>, <c>DocumentTypeResources</c> x2) that list
/// types for an LLM and never read a caller's per-type grants. Deliberately carries no counterpart to
/// <see cref="DocumentTypeDto.ResourcePermissions"/>, so the "empty dictionary means not asked, not no grants"
/// ambiguity <see cref="IDocumentTypeAppService.GetVisibleAsync"/> used to carry through its boolean flag cannot
/// be expressed here at all (#636).
/// </summary>
public class DocumentTypeSummaryDto : EntityDto<Guid>
{
    public string TypeCode { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
}
