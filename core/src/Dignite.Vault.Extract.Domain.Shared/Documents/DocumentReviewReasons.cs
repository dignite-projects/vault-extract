using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Set of document <b>review reasons</b>, the objective unresolved-problem axis answering "why does
/// this document need operator attention". Orthogonal to the <b>disposition stage</b>
/// (<see cref="DocumentReviewDisposition"/>): this set is maintained only by pipelines / evaluators,
/// and each bit is owned by <b>exactly one</b> stage that sets / clears it independently without
/// overwriting others. <c>None</c> means there are no unresolved reasons.
/// <para>
/// Reasons are split into two classes; see <see cref="ReviewReasonPolicy"/>: <b>blocking</b> reasons
/// block Ready and make the document unavailable to downstream consumers, while <b>non-blocking</b>
/// reasons do not block downstream use and only enter the operator queue. Adding a future reason means
/// adding one member; non-blocking reasons need no extra changes, and blocking reasons are ORed into
/// <see cref="ReviewReasonPolicy.Blocking"/>. The decision structure stays unchanged.
/// </para>
/// Persisted as a single <c>[Flags]</c> int column, portable across databases (#206) and requiring no
/// JOIN on read paths.
/// </summary>
[Flags]
public enum DocumentReviewReasons
{
    /// <summary>No unresolved reasons.</summary>
    None = 0,

    /// <summary>
    /// Unresolved classification (low confidence / cannot classify). <b>blocking</b>: without a
    /// confirmed type, the document is unavailable to downstream consumers and Ready is blocked.
    /// Maintained by the classification stage, replacing the old
    /// <c>DocumentReviewStatus.PendingReview</c>.
    /// </summary>
    UnresolvedClassification = 1 << 0,

    /// <summary>
    /// Required fields declared by this type (<c>FieldDefinition.IsRequired</c>) were not extracted.
    /// <b>non-blocking</b>: the document still becomes Ready and emits <c>DocumentReadyEto</c>; it only
    /// enters the operator "needs completion" queue. Maintained by the field extraction stage.
    /// </summary>
    MissingRequiredFields = 1 << 1,

    /// <summary>
    /// A container (#346) was detected but its born-digital segmentation could not be completed cleanly — the
    /// LLM produced an untrusted split, fewer than two document slices, or more than
    /// <c>MaxSegmentsPerDocument</c>. <b>non-blocking</b>: the container itself is already Ready (it carries no
    /// type to gate), so this only routes it into the operator queue so a human can split / reclassify it instead
    /// of it silently producing zero sub-documents. Maintained by the segmentation job.
    /// </summary>
    SegmentationIncomplete = 1 << 2,

    /// <summary>
    /// Another document in the same layer + document type has the same <see cref="Documents.Document.FieldFingerprint"/>
    /// (the SHA-256 of this type's normalized unique-key field values, #411) — a likely duplicate re-upload of the
    /// same business entity (e.g. the same receipt scanned twice). <b>blocking</b>: the document is withheld from
    /// Ready so downstream never consumes a suspected duplicate, and it enters the operator review queue. The
    /// operator either releases it (<c>AllowDuplicateAsync</c> — not a duplicate / acceptable re-upload) or discards
    /// it (<c>DeleteAsync</c> — confirmed duplicate). Maintained by the field extraction stage, which only sets it
    /// when <see cref="Documents.Document.DuplicateAllowed"/> is false (so an operator override survives re-extraction).
    /// </summary>
    DuplicateSuspected = 1 << 3,

    /// <summary>
    /// The document's Markdown exceeds <c>VaultExtractBehaviorOptions.MaxFieldExtractionMarkdownLength</c>, so the field
    /// extraction stage <b>declined to call the LLM at all</b> (#491) rather than send an unbounded prompt body. Only ever
    /// set when the document's type actually declares field definitions — a type with no fields issues no call, so it can
    /// never be "incomplete". <b>blocking</b>: <c>ExtractedFields</c> would be empty for a reason unrelated to the
    /// document's content, and downstream cannot distinguish "this type declares no fields" from "we declined to look", so
    /// Ready is withheld until a human resolves it. Consumers that only want the text can still subscribe to the earlier
    /// <c>OCRCompletedEto</c>. Cleared by a later successful extraction (e.g. after the host raises the ceiling), by
    /// reclassification to a type without fields, or by an operator entering the values by hand
    /// (<c>DocumentAppService.UpdateExtractedFieldsAsync</c> — the human has taken over the work the LLM declined).
    /// Maintained by the field extraction stage.
    /// </summary>
    FieldExtractionIncomplete = 1 << 4,

    /// <summary>
    /// One or more type-bound fields carry a <b>validation warning</b> (#527): unified field extraction returned the
    /// field's extracted value <b>together with</b> a warning that the value failed a validation rule declared in the
    /// field's prompt (e.g. a bank-statement balance-consistency check). The extracted value is preserved on
    /// <c>DocumentExtractedField</c> so an operator can compare it with the source; the warning text lives on the
    /// separate <c>DocumentFieldValidationWarning</c> child collection and never enters field values, search indexes,
    /// exports, or event payloads (#527 §11). <b>blocking</b>: the extracted result is not yet trustworthy, so Ready is
    /// withheld and the document enters the operator review queue until either a later clean re-extraction replaces the
    /// warning set with an empty one, or an operator explicitly resolves the warnings after comparing the source file.
    /// Independent of the non-blocking <see cref="MissingRequiredFields"/> — a warned field keeps its value and is never
    /// mapped to a missing field, and multiple reasons may coexist. Maintained by the field extraction stage, coupled to
    /// the warning collection in <c>Document.ReplaceFieldValidationWarnings</c> so the bit and the collection cannot diverge.
    /// </summary>
    FieldValidationWarning = 1 << 5
}
