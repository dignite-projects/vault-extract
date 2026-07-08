using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.Documents;

public interface IDocumentAppService : IApplicationService
{
    Task<DocumentDto> GetAsync(Guid id);

    Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input);

    Task<DocumentDto> UploadAsync(UploadDocumentInput input);

    Task<IRemoteStreamContent> GetBlobAsync(Guid id);

    /// <summary>
    /// Serves a retained embedded-figure image (#477) referenced from the document's Markdown as
    /// <c>figures/{hash}.{ext}</c>. <paramref name="fileName"/> is that reference's last segment; its content hash
    /// (the name without the cosmetic extension) is resolved against the document's retained-figure manifest.
    /// </summary>
    Task<IRemoteStreamContent> GetFigureAsync(Guid id, string fileName);

    Task DeleteAsync(Guid id);

    Task PermanentDeleteAsync(Guid id);

    Task RestoreAsync(Guid id);

    Task<DocumentDto> ConfirmClassificationAsync(Guid id, ConfirmClassificationInput input);

    /// <summary>
    /// Operator actively corrects classification: overwriting to a new type is allowed in any state.
    /// Behavior: writes DocumentTypeCode, ReviewDisposition=Confirmed, Confidence=1.0, clears the UnresolvedClassification reason,
    /// and publishes <see cref="Abstractions.Documents.DocumentClassifiedEto"/> through ABP transactional outbox.
    /// Downstream business consumers may subscribe to DocumentClassifiedEto to rerun their own field extraction and handle at-least-once redelivery
    /// idempotently by <c>(DocumentId, EventType, EventTime)</c>.
    /// </summary>
    Task<DocumentDto> ReclassifyAsync(Guid id, ReclassifyDocumentInput input);

    /// <summary>
    /// Operator rejects a document awaiting review (#284: reason is <b>required</b>): sets ReviewDisposition to Rejected,
    /// writes RejectionReason, and moves the document to Failed lifecycle. Keeps the original file, extracted Markdown,
    /// field values, and objective review reasons for audit.
    /// <para>
    /// <b>Rejection is recoverable, not terminal</b> (#237): an operator may later Reclassify the same document to assign a type,
    /// moving it back to Confirmed, deriving Ready again, and re-emitting <see cref="Abstractions.Documents.DocumentReadyEto"/>.
    /// This path does not provide rerun / source-file replacement; retry is done by operator re-upload.
    /// </para>
    /// </summary>
    Task<DocumentDto> RejectReviewAsync(Guid id, RejectReviewInput input);

    /// <summary>
    /// Operator resolves a <see cref="DocumentReviewReasons.DuplicateSuspected"/> flag by deciding the document is
    /// <b>not</b> a duplicate (or is an acceptable re-upload) (#411): sets the durable <c>DuplicateAllowed</c>
    /// override, clears the blocking duplicate reason, and re-derives lifecycle — which releases the document to Ready
    /// and emits <see cref="Abstractions.Documents.DocumentReadyEto"/> if no other blocking reason remains. The
    /// override survives later re-extraction so the document is not re-flagged. The opposite resolution — confirming
    /// the duplicate — is the existing <see cref="DeleteAsync"/> (soft-delete → <c>DocumentDeletedEto</c> → downstream
    /// retracts).
    /// </summary>
    Task<DocumentDto> AllowDuplicateAsync(Guid id);

    Task RetryPipelineAsync(Guid id, RetryPipelineInput input);

    /// <summary>
    /// "Re-recognize" (#263): asks AI to rerun the automatic classification workflow on **existing Markdown**,
    /// cascading field re-extraction, **without rerunning OCR**.
    /// <para>
    /// Differs from <see cref="ReclassifyAsync"/> (operator **manually specifies** type, persists synchronously, no LLM)
    /// and <see cref="RetryPipelineAsync"/> (only <c>Failed</c> runs are retryable): this path re-enqueues the classification job
    /// for any document with **completed text extraction**, letting the LLM reclassify automatically using the latest type / field descriptions.
    /// High confidence publishes <see cref="Abstractions.Documents.DocumentClassifiedEto"/> through transactional outbox and
    /// <c>FieldExtractionEventHandler</c> cascades field re-extraction; low confidence enters the manual-review queue.
    /// </para>
    /// <para>
    /// Warning: this **overwrites** existing classification results, including operator-confirmed types, and field values edited by operators
    /// when cascading re-extraction runs. Caller UI must confirm first. Rejected when the document is in the trash, has no Markdown yet,
    /// or classification is already in progress.
    /// </para>
    /// </summary>
    Task RerecognizeAsync(Guid id);

    /// <summary>
    /// "Field re-extraction only" (#289 scenario 2, single-document version): reruns only type-bound field extraction
    /// (<c>field-extraction</c> pipeline) on the **existing classification**, with **no reclassification and no OCR rerun**.
    /// This is the lightweight detail-page button distinct from "re-recognize", used when field definitions changed and classification should stay untouched.
    /// <para>
    /// Differs from <see cref="RerecognizeAsync"/> (destructive reclassification + cascade): this path is a safe leaf operation,
    /// replacing only the whole field value set. It may overwrite operator-edited field values, but at lower cost.
    /// After completion, the engine emits <see cref="Abstractions.Documents.FieldsExtractedEto"/>.
    /// Rejected when the document is in the trash, unclassified (no type), has no Markdown yet, or field extraction is already in progress.
    /// </para>
    /// </summary>
    Task ReextractFieldsAsync(Guid id);

    /// <summary>
    /// Operator edits type-bound field extraction results (individual corrections). Replaces the document's field value set as a whole.
    /// Each key must be a <see cref="FieldDefinition.Name"/> defined under this document's layer and DocumentType.
    /// After completion, reuses <see cref="Abstractions.Documents.FieldsExtractedEto"/> for re-delivery; downstream consumers absorb it
    /// idempotently by <c>(DocumentId, EventType, EventTime)</c> and pull back latest field values.
    /// Large-scale errors should use text-extraction rerun / re-upload instead of bulk patching through this path.
    /// </summary>
    Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input);

    /// <summary>
    /// Reassigns the document's cabinet (#257): a manual organization dimension, orthogonal to pipelines,
    /// triggering no later Run and emitting no export event.
    /// <paramref name="input"/>.CabinetId null means remove from cabinet (uncategorized); non-null must reference an existing cabinet in the current layer.
    /// </summary>
    Task<DocumentDto> UpdateCabinetAsync(Guid id, UpdateDocumentCabinetInput input);
}
