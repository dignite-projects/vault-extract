using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.Documents;

public interface IDocumentAppService : IApplicationService
{
    Task<DocumentDto> GetAsync(Guid id);

    /// <summary>
    /// MCP not-found folding, raised to the use case (#636): <c>null</c> for not-found (including cross-tenant,
    /// filtered out by the ambient <c>IMultiTenant</c> filter) and for an in-tenant document outside the caller's
    /// per-type Read scope alike — the two causes <see cref="GetAsync"/>'s MCP callers used to fold together
    /// locally by each catching <c>EntityNotFoundException</c> and <c>AbpAuthorizationException</c> around it.
    /// Still throws <see cref="Volo.Abp.Authorization.AbpAuthorizationException"/> when the caller lacks entry
    /// (<c>Documents.Default</c>): that is a caller-wide fact unrelated to any one id, and folding it into
    /// <c>null</c> would make "no permission at all" read the same as "wrong id" — a diagnosability regression MCP
    /// callers should not have. <see cref="GetAsync"/> keeps its own throwing contract for the REST surface; this
    /// method exists so an adapter with no REST-style error semantics does not need a try/catch around it.
    /// </summary>
    Task<DocumentDto?> FindForCallerAsync(Guid id);

    Task<PagedResultDto<DocumentListItemDto>> GetListAsync(GetDocumentListInput input);

    Task<DocumentDto> UploadAsync(UploadDocumentInput input);

    Task<IRemoteStreamContent> GetBlobAsync(Guid id);

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

    /// <summary>
    /// #527 §9: the operator resolves the field validation warnings on the selected fields after comparing the source
    /// file. Removes those warnings and clears the blocking <c>FieldValidationWarning</c> review reason only when none
    /// remain, then re-derives lifecycle so the document may transition to Ready. Rejected while field extraction is
    /// pending/running (an in-flight result would overwrite the decision). Distinct from <see cref="UpdateExtractedFieldsAsync"/>,
    /// which does not clear warnings.
    /// </summary>
    Task<DocumentDto> ResolveFieldValidationWarningsAsync(Guid id, ResolveFieldValidationWarningsInput input);

    /// <summary>
    /// #657: the operator's explicit declaration that field entry is complete for a document #491 declined to
    /// auto-extract for being too large. Clears the blocking <c>FieldExtractionIncomplete</c> review reason and
    /// re-derives lifecycle so the document may transition to Ready. This is the <b>only</b> path that clears the
    /// reason — <see cref="UpdateExtractedFieldsAsync"/> does not, however many (or how few) fields it submits, so
    /// that an edit can never release a partially-filled document as a side effect. "None of this type's fields
    /// apply" is this same call performed with nothing entered first, not a separate verdict.
    /// </summary>
    Task<DocumentDto> ConfirmFieldEntryAsync(Guid id);

    Task RetryPipelineAsync(Guid id, RetryPipelineInput input);

    /// <summary>
    /// "Re-recognize" (#263): asks AI to rerun the automatic classification workflow on **existing Markdown**,
    /// cascading field re-extraction, **without rerunning OCR**.
    /// <para>
    /// Differs from <see cref="ReclassifyAsync"/> (operator **manually specifies** type, persists synchronously, no LLM)
    /// and <see cref="RetryPipelineAsync"/> (only <c>Failed</c> runs are retryable): this path re-enqueues the classification job
    /// for any document with **completed text extraction**, letting the LLM reclassify automatically using the latest type / field descriptions.
    /// High confidence publishes <see cref="Abstractions.Documents.DocumentClassifiedEto"/> through transactional outbox while the
    /// classification stage schedules the cascade field re-extraction run transactionally (#527 §8); low confidence enters the manual-review queue.
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
    /// After completion, the run's lifecycle re-derivation round-trips the document through Processing and, when it derives
    /// Ready again, re-fires <see cref="Abstractions.Documents.DocumentReadyEto"/> — the pipeline's egress for this path.
    /// A re-extraction that newly raises a blocking review reason (e.g. a suspected duplicate) ends in PendingReview and fires nothing.
    /// Rejected when the document is in the trash, unclassified (no type), has no Markdown yet, or field extraction is already in progress.
    /// </para>
    /// </summary>
    Task ReextractFieldsAsync(Guid id);

    /// <summary>
    /// Operator edits type-bound field extraction results (individual corrections). Replaces the document's field value set as a whole.
    /// Each key must be a <see cref="FieldDefinition.Name"/> defined under this document's layer and DocumentType.
    /// #657: never touches the blocking <c>FieldExtractionIncomplete</c> reason, however many (or how few) fields it
    /// submits — <see cref="ConfirmFieldEntryAsync"/> is the only path that clears it, a deliberate operator act
    /// independent of what was submitted.
    /// #650: when the document was already <c>Ready</c> both before and after this edit, re-publishes
    /// <see cref="Abstractions.Documents.DocumentReadyEto"/> — an operator edit on an already-Ready document changes
    /// consumable content with no lifecycle transition to announce it, so this is the contract's "pull it again"
    /// signal. Downstream consumers absorb it idempotently by <c>(DocumentId, EventType, EventTime)</c> and pull back
    /// latest field values. A transition *into* Ready caused by this same edit (e.g. a corrected unique-key value
    /// clearing <c>DuplicateSuspected</c>) is announced once, by the existing lifecycle re-derivation, not doubled here.
    /// Large-scale errors should use text-extraction rerun / re-upload instead of bulk patching through this path.
    /// </summary>
    Task<DocumentDto> UpdateExtractedFieldsAsync(Guid id, UpdateExtractedFieldsInput input);

    /// <summary>
    /// Operator correction of already-extracted <see cref="Document.Markdown"/> (#555): fixes a small OCR /
    /// parsing error, a separate path from the pipeline's write-once extraction write (<c>SetMarkdown</c> is
    /// unchanged and still refuses a second write there).
    /// <para>
    /// <paramref name="input"/>.Reprocess toggles what happens after the Markdown is overwritten:
    /// <c>true</c> re-runs field extraction (the same mechanism <see cref="ReextractFieldsAsync"/> uses), which
    /// round-trips the lifecycle through Processing and, when it derives Ready again, re-fires <see cref="Abstractions.Documents.DocumentReadyEto"/>.
    /// It does <b>not</b> touch classification or segmentation — those have their own independent entry points
    /// (<see cref="RerecognizeAsync"/>). <c>false</c> (default) writes the Markdown only: no re-extraction, and
    /// no event is fired at all — a deliberate accepted trade-off; a downstream consumer that already pulled
    /// the document via <c>DocumentReadyEto</c> will not know the content changed until it re-fetches.
    /// </para>
    /// <para>
    /// Never touches <see cref="Document.Title"/>, <see cref="Document.Language"/>,
    /// <see cref="Document.ExtractionMetadata"/>, or <see cref="Document.FieldFingerprint"/> — those stay as
    /// they were from the original extraction. Forbidden on a container document (<see cref="Document.IsContainer"/>):
    /// it runs no field extraction and its Markdown is only a provenance anchor. Rejected when the document is
    /// in the trash, has no Markdown yet (nothing to correct), or — only when <c>Reprocess</c> is <c>true</c> —
    /// is unclassified (nothing to extract fields against) or field extraction is already in progress. No
    /// history table: the correction trail is carried by ABP entity audit logging, the same reasoning as
    /// <see cref="RejectReviewAsync"/> and <see cref="ResolveFieldValidationWarningsAsync"/>.
    /// </para>
    /// </summary>
    Task<DocumentDto> UpdateMarkdownAsync(Guid id, UpdateMarkdownInput input);

    /// <summary>
    /// Reassigns the document's cabinet (#257): a manual organization dimension, orthogonal to pipelines,
    /// triggering no later Run and emitting no export event.
    /// <paramref name="input"/>.CabinetId null means remove from cabinet (uncategorized); non-null must reference an existing cabinet in the current layer.
    /// </summary>
    Task<DocumentDto> UpdateCabinetAsync(Guid id, UpdateDocumentCabinetInput input);
}
