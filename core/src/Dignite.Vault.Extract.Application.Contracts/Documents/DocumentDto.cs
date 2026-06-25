using System;
using System.Collections.Generic;
using System.Text.Json;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents;

public class DocumentDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public FileOriginDto FileOrigin { get; set; } = default!;

    /// <summary>Owning cabinet (#194). null means uncategorized. The frontend maps cabinet names from the cabinet list.</summary>
    public Guid? CabinetId { get; set; }

    /// <summary>
    /// Provenance link for a Scenario B sub-document (#306): when this document was derived from a constituent
    /// of another document, the id of that source document; <c>null</c> for normally-uploaded documents.
    /// </summary>
    public Guid? OriginDocumentId { get; set; }

    /// <summary>
    /// Whether this document is a <b>container</b> (#346): a parent bundling several independent documents that
    /// runs no type-bound field extraction itself. <c>true</c> means downstream must <b>not</b> build a business
    /// record from this document — its real records are its sub-documents (query <c>OriginDocumentId == this.Id</c>).
    /// A container has no <see cref="DocumentTypeCode"/> and no <see cref="ExtractedFields"/>.
    /// </summary>
    public bool IsContainer { get; set; }

    public string? DocumentTypeCode { get; set; }
    public DocumentLifecycleStatus LifecycleStatus { get; set; }
    public DocumentReviewDisposition ReviewDisposition { get; set; }

    /// <summary>Set of review reasons (#284, <c>[Flags]</c>). Clients render reason badges directly and do not infer them.</summary>
    public DocumentReviewReasons ReviewReasons { get; set; }

    /// <summary>Whether operator attention is needed (#284): <c>ReviewReasons != None</c> and <c>ReviewDisposition != Rejected</c>. Rejected documents keep objective reasons, but the operator has already handled them, so they no longer count as needing attention. Exposed by the server to avoid client inference.</summary>
    public bool RequiresReview { get; set; }

    /// <summary>Structured review-reason details (#284). Detail views are rich while list views are thin, so this is assembled only for single-document details; null when there are no unresolved reasons.</summary>
    public List<ReviewReasonDetailDto>? ReviewReasonDetails { get; set; }

    /// <summary>Operator rejection reason (#284), populated only when <see cref="ReviewDisposition"/> is Rejected.</summary>
    public string? RejectionReason { get; set; }

    public double ClassificationConfidence { get; set; }

    /// <summary>
    /// Display title, written after the text extraction pipeline run succeeds.
    /// Historical documents created before the migration may be null, so the UI must fall back to
    /// <see cref="FileOriginDto.OriginalFileName"/>.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Structured Markdown document content, written after the text extraction pipeline run succeeds.
    /// The frontend can render it directly; when plain text is needed, the frontend strips it or the
    /// backend projects it through <c>MarkdownStripper.Strip</c>.
    /// </summary>
    public string? Markdown { get; set; }

    /// <summary>
    /// Document language (ISO 639-1 / IETF tag), detected and written during text extraction. null
    /// when not detected.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Whether text extraction is <b>complete</b> (#268). <c>true</c> means all content was captured
    /// (the default, and historical documents are also treated as complete); <c>false</c> means
    /// content is known to be missing, such as truncated OCR output, duplicate-guard drops, or pages
    /// in a multi-page PDF that could not be transcribed. Downstream consumers decide whether to
    /// accept, degrade, or send for manual review; the channel layer does not block for them.
    /// Note: this is a <b>quality signal</b>, distinct from internal extraction provenance such as
    /// provider name / archived BlobName, which is intentionally not exposed.
    /// </summary>
    public bool ExtractionIsComplete { get; set; } = true;

    /// <summary>Short diagnostic when extraction is incomplete (<see cref="ExtractionIsComplete"/> is false); <c>null</c> when complete.</summary>
    public string? ExtractionIncompleteReason { get; set; }

    /// <summary>
    /// Type-bound field extraction results (field architecture v2). Key = FieldName, with the same
    /// shape as <see cref="FieldDefinitionDto.Name"/>. The source layer is determined by
    /// <see cref="TenantId"/>: host documents use host field definitions, while tenant documents use
    /// that tenant's field definitions. null when not yet extracted or when there are no type-bound
    /// fields.
    /// </summary>
    public Dictionary<string, JsonElement>? ExtractedFields { get; set; }

    public DateTime CreationTime { get; set; }

    // Run records are exposed through IDocumentPipelineRunAppService.GetListAsync(documentId) after
    // #216 split them into an independent aggregate root.
}
