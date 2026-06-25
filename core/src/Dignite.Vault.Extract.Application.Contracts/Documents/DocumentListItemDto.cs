using System;
using System.Collections.Generic;
using System.Text.Json;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents;

public class DocumentListItemDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public FileOriginDto FileOrigin { get; set; } = default!;

    /// <summary>Owning cabinet (#194). null means uncategorized. The frontend maps cabinet names from the cabinet list.</summary>
    public Guid? CabinetId { get; set; }

    /// <summary>
    /// Provenance link for a Scenario B sub-document (#306): when this document was derived from a constituent
    /// of another document, the id of that source document; <c>null</c> for normally-uploaded documents. Mirrors
    /// <see cref="DocumentDto.OriginDocumentId"/> on the list surface (#350) so the operator UI can offer a
    /// "view sub-documents" affordance without a detail fetch.
    /// </summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>
    /// Whether this document is a <b>container</b> (#346): a parent bundling several independent documents that
    /// runs no type-bound field extraction itself. Mirrors <see cref="DocumentDto.IsContainer"/> on the list
    /// surface (#350) so list views can render a "Bundle" badge; downstream must <b>not</b> build a business
    /// record from a container — its real records are its sub-documents (query <c>OriginDocumentId == this.Id</c>).
    /// </summary>
    public bool IsContainer { get; set; }

    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewDisposition ReviewDisposition { get; set; }

    /// <summary>Set of review reasons (#284, <c>[Flags]</c>). List views use reason badges to distinguish classification confirmation from missing field completion.</summary>
    public DocumentReviewReasons ReviewReasons { get; set; }

    /// <summary>Whether operator attention is needed (#284): <c>ReviewReasons != None</c> and <c>ReviewDisposition != Rejected</c>. List views are thin and omit details; details appear only on the detail page.</summary>
    public bool RequiresReview { get; set; }

    public double ClassificationConfidence { get; set; }

    /// <summary>
    /// Display title. Historical documents created before the migration may be null, so the UI must
    /// fall back to <see cref="FileOriginDto.OriginalFileName"/>.
    /// </summary>
    public string? Title { get; set; }

    public DateTime CreationTime { get; set; }

    /// <summary>
    /// Soft-deletion time. Populated only when <see cref="GetDocumentListInput.IsDeleted"/> = true
    /// (recycle-bin view).
    /// </summary>
    public DateTime? DeletionTime { get; set; }

    /// <summary>
    /// Type-bound field extraction results for this document (field architecture v2), with the same
    /// shape as <see cref="DocumentDto.ExtractedFields"/>. key = <see cref="FieldDefinition.Name"/>;
    /// value is the raw <see cref="JsonElement"/> preserving the declared type. null when not
    /// extracted. List queries always carry this value; consumers decide how to present it, such as
    /// Angular list field columns by DocumentTypeCode or MCP outbound surfaces. LLM-facing outbound
    /// surfaces (MCP) decide whether PromptBoundary wrapping is needed by
    /// <see cref="JsonElement.ValueKind"/>, which is a transport concern and is not applied in this
    /// common DTO.
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }
}
