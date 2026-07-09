using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents;

public class Document : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    // Multi-tenancy
    public virtual Guid? TenantId { get; private set; }

    /// <summary>
    /// File origin information (immutable): user-uploaded documents always have one (their own upload blob);
    /// derived sub-documents spawned from a container carry none (<c>null</c>) — Markdown is always seeded from
    /// the segment slice, never extracted from a blob.
    /// </summary>
    public virtual FileOrigin? FileOrigin { get; private set; }

    /// <summary>
    /// Owning cabinet (manual organization dimension, #194). Nullable; null means "uncategorized".
    /// Set manually by the operator during upload, and <b>orthogonal to pipelines</b>: OCR / classification / field extraction do not read or write this field
    /// (otherwise cabinets would collapse into a second DocumentType, binding manual organization to AI content classification).
    /// References the <see cref="Cabinet"/> aggregate root through a nullable Guid foreign key (DDD reference-by-id, no navigation property).
    /// </summary>
    public virtual Guid? CabinetId { get; private set; }

    /// <summary>
    /// Internal document type association, written after a successful classification pipeline run. References <see cref="DocumentType"/>.Id (DDD reference-by-id, no navigation property).
    /// null means there is currently no confirmed or usable document type; manual-review state is expressed by <see cref="ReviewDisposition"/> / <see cref="ReviewReasons"/>.
    /// <para>
    /// Internally associated by immutable Id (#207); external wire formats (REST / MCP / ETO) still output the <c>DocumentTypeCode</c> string.
    /// Read paths join <see cref="DocumentType"/> to resolve the current, or last-known after soft delete, TypeCode. TypeCode renames no longer cascade to this table.
    /// </para>
    /// </summary>
    public virtual Guid? DocumentTypeId { get; private set; }

    /// <summary>
    /// Coarse document lifecycle state.
    /// Derived by DocumentPipelineRunManager from key pipeline run results; not set directly by the application layer.
    /// </summary>
    public virtual DocumentLifecycleStatus LifecycleStatus { get; private set; }

    /// <summary>
    /// Manual review <b>disposition phase</b> (operator action axis, #284): NotReviewed (default), Confirmed (operator-confirmed type),
    /// or Rejected (operator rejection, recoverable; a later Reclassify moves it back to Confirmed).
    /// Orthogonal to <b>review reasons</b> (<see cref="ReviewReasons"/>): this field is written only by operator actions.
    /// Whether operator attention is required is derived by <see cref="ReviewReasonPolicy.RequiresAttention(DocumentReviewReasons, DocumentReviewDisposition)"/>
    /// (<c>ReviewReasons != None and this field != Rejected</c>). Rejected suppresses attention because the operator already handled it; otherwise the reason axis drives attention.
    /// </summary>
    public virtual DocumentReviewDisposition ReviewDisposition { get; private set; }

    /// <summary>
    /// Review reason set (objective unresolved-problem axis, #284): why operator attention is required. Each bit is maintained by exactly one pipeline phase
    /// (UnresolvedClassification <- classification phase, MissingRequiredFields <- field extraction phase; bitwise set/clear avoids overwriting between phases).
    /// Blocking reasons (see <see cref="ReviewReasonPolicy"/>) prevent Ready through
    /// <see cref="Pipelines.DocumentPipelineRunManager.DeriveLifecycleAsync"/>;
    /// non-blocking reasons only enter the operator queue and do not block downstream consumers.
    /// </summary>
    public virtual DocumentReviewReasons ReviewReasons { get; private set; }

    /// <summary>
    /// Extracted structured Markdown content, written after a successful text extraction pipeline run and immutable afterward.
    /// This is the only text payload on Document; downstream consumers that need plain text project it through <see cref="MarkdownStripper.Strip"/>.
    /// </summary>
    public virtual string? Markdown { get; private set; }

    /// <summary>
    /// Display title for the document, written after a successful text extraction pipeline run and immutable afterward.
    /// Extracted from <see cref="Markdown"/> by <see cref="MarkdownTitleExtractor"/>; if extraction fails, upstream falls back to the file name without extension.
    /// Historical records from before the migration may be null; read paths should fall back to <see cref="FileOrigin"/>?.OriginalFileName / <see cref="FileOrigin"/>?.BlobName.
    /// </summary>
    public virtual string? Title { get; private set; }

    /// <summary>
    /// Document classification confidence (0.0 to 1.0), captured from the latest successful classification run.
    /// When <see cref="DocumentTypeId"/> is null, this value is 0; manual-review state is expressed by <see cref="ReviewDisposition"/> / <see cref="ReviewReasons"/>.
    /// Operator confirmation (<see cref="DocumentReviewDisposition.Confirmed"/>) always writes 1.0.
    /// </summary>
    public virtual double ClassificationConfidence { get; private set; }

    /// <summary>
    /// Rejection reason entered by the operator during review rejection (#284: split from the former ClassificationReason; <b>required</b> when rejecting).
    /// Has a value only when <see cref="ReviewDisposition"/> = Rejected; its single meaning is the human-entered rejection note,
    /// no longer doubling as an AI classification explanation. Length is constrained by <see cref="DocumentConsts.MaxRejectionReasonLength"/>.
    /// </summary>
    public virtual string? RejectionReason { get; private set; }

    // === Field architecture v2: system common fields (top-level typed columns filled by pipeline phases) ===

    /// <summary>Document language (ISO 639-1 / IETF tag). Detected by OCR / extraction phases; affects downstream prompt language selection.</summary>
    public virtual string? Language { get; private set; }

    /// <summary>
    /// Text extraction provenance metadata (#210): winning provider name + archived native payload manifest.
    /// Domain-owned typed value object -> JSON column, decoupled from provider contracts.
    /// Raw spatial signals such as bbox / cell data <b>stay in blob storage</b>; this field stores only the manifest. Written after a successful text extraction pipeline run; historical records may be null.
    /// </summary>
    public virtual DocumentParseMetadata? ExtractionMetadata { get; private set; }

    // === Duplicate detection (#411) ===

    /// <summary>
    /// Content-derived stable key for duplicate re-upload detection (#411): the SHA-256 (lowercase hex) of this
    /// document type's <b>normalized unique-key field values</b> (the <c>FieldDefinition.IsUniqueKey</c> set). Two
    /// documents in the same layer + same <see cref="DocumentTypeId"/> sharing this value are likely the same
    /// business entity (e.g. the same receipt scanned twice). Written by the field extraction stage via
    /// <see cref="SetFieldFingerprint"/>; <c>null</c> when the type declares no unique-key fields, or when not all of
    /// them have an extracted value (a partial key is not fingerprinted, to avoid false collisions). Derived from
    /// <see cref="ExtractedFieldValues"/> and therefore recomputed on every re-extraction; cleared whenever the type
    /// is retracted or the document becomes a container.
    /// </summary>
    public virtual string? FieldFingerprint { get; private set; }

    /// <summary>
    /// Durable operator override (#411): the operator reviewed a <see cref="DocumentReviewReasons.DuplicateSuspected"/>
    /// flag and decided this document is <b>not</b> a duplicate (or is an acceptable re-upload). When true, the field
    /// extraction stage does <b>not</b> re-raise <c>DuplicateSuspected</c> on subsequent re-extractions (#289 bulk /
    /// manual reclassify), so the operator's decision survives. Reset to false whenever the document is
    /// (re)classified or its type is retracted — a new type context is a fresh duplicate-review decision. Set by
    /// <c>AllowDuplicateAsync</c>.
    /// </summary>
    public virtual bool DuplicateAllowed { get; private set; }

    // === Container marker (#346) ===

    /// <summary>
    /// Whether this document is a <b>container</b> (#346): a parent whose content is several independent documents
    /// (a multi-type bundle, or multiple instances of one type), so it runs <b>no</b> type-bound field extraction
    /// itself — each constituent is delegated to a sub-document. Set by <see cref="MarkAsContainer"/> when the
    /// classification stage reports a container; <c>false</c> for normal single documents. A generic, strongly-typed
    /// truth-source marker (not a business field, not a generic extension bag), exposed at the egress so downstream
    /// skips building a record from a container and follows its sub-documents instead.
    /// </summary>
    public virtual bool IsContainer { get; private set; }

    /// <summary>
    /// Whether the unified sub-document detection pass (#371) has reached a <b>terminal SUCCESS</b> for this
    /// document's current recognition — its constituents were split and persisted, or it was confirmed to have
    /// nothing standalone to route. Set by <see cref="MarkSegmented"/> in the same transaction as the segment rows,
    /// and used by <c>DocumentSegmentationJob</c> as the precise resume gate (skip the LLM split when set), replacing
    /// the imperfect "infer completion from segment-row Kind" heuristic that could not tell an embedded-run figure
    /// row apart from a container-run figure row (#372/#377). It is <b>cleared on every container↔concrete
    /// transition</b> — both directions — through the single <see cref="SetContainerFlag"/> choke point (#378/#379):
    /// a concrete→container re-recognition so the new container runs its split exactly once, and a container→concrete
    /// reclassify so the now-concrete document's own embedded-document routing can run instead of being skipped by a
    /// stale marker. Failure / incomplete / byte-identical outcomes do NOT set it, so a retry re-runs the split.
    /// Internal pipeline state — not exposed at the egress.
    /// </summary>
    public virtual bool IsSegmented { get; private set; }

    // === Scenario B sub-document back-reference (#306 / generalized in #346) ===

    /// <summary>
    /// When this document was derived from a constituent of another document (#306 / #346, Scenario B), the id of
    /// that <b>source</b> document; <c>null</c> for normally-uploaded documents. A peer back-reference
    /// (reference-by-id, no navigation property, no FK cascade): the derived document has a fully independent
    /// lifecycle and outlives the source. Exposed at the egress so downstream can follow it for provenance.
    /// </summary>
    public virtual Guid? OriginDocumentId { get; private set; }

    /// <summary>
    /// Content-derived stable key of the source constituent this document was derived from (#306 figure path /
    /// #346 born-digital path): the SHA-256 of the Markdown slice text. NOT bbox (which drifts, #210). Unique
    /// together with <see cref="OriginDocumentId"/> so re-extraction / routing retry never duplicate-spawn.
    /// <c>null</c> for normally-uploaded documents.
    /// </summary>
    public virtual string? OriginConstituentKey { get; private set; }

    // --- Aggregate-internal field value collection (field architecture v2 / Issue #206) ---

    private readonly List<DocumentExtractedField> _extractedFieldValues = new();

    /// <summary>
    /// Type-bound field extraction results (field architecture v2): a child collection with one row per field value, the sole truth source for field value queries and persistence.
    /// <para>
    /// A single Document runs field extraction in exactly one layer, determined by <see cref="TenantId"/>; there is no bucketing and no cross-layer naming conflict.
    /// </para>
    /// The export DTO / MCP / REST <c>ExtractedFields</c> dictionary is assembled on demand from these rows by the App / Mapper layer
    /// (see <see cref="DocumentExtractedField.ToJsonElement"/>), preserving wire-format compatibility with the old JSON column.
    /// </summary>
    public virtual IReadOnlyCollection<DocumentExtractedField> ExtractedFieldValues => _extractedFieldValues.AsReadOnly();

    protected Document()
    {
    }

    public Document(
        Guid id,
        Guid? tenantId,
        FileOrigin? fileOrigin,
        Guid? cabinetId = null)
        : base(id)
    {
        TenantId = tenantId;
        FileOrigin = fileOrigin;
        CabinetId = cabinetId;
        LifecycleStatus = DocumentLifecycleStatus.Uploaded;
    }

    /// <summary>
    /// Creates a <b>derived</b> document spawned from a constituent of <paramref name="originDocumentId"/>
    /// (#306 / #346, Scenario B): an embedded figure (image path) or a Markdown slice (born-digital path). It is a
    /// normal peer <see cref="Document"/> that runs the full pipeline + egress; the only difference is the
    /// back-reference (<see cref="OriginDocumentId"/> / <see cref="OriginConstituentKey"/>).
    /// <paramref name="fileOrigin"/> is <c>null</c> for every derived document: a sub-document has no file of its
    /// own to parse or download. Markdown is still seeded from the segment SliceText (seed precedence).
    /// </summary>
    public static Document CreateDerived(
        Guid id,
        Guid? tenantId,
        FileOrigin? fileOrigin,
        Guid originDocumentId,
        string originConstituentKey)
    {
        var document = new Document(id, tenantId, fileOrigin)
        {
            OriginDocumentId = Check.NotDefaultOrNull<Guid>(originDocumentId, nameof(originDocumentId)),
            OriginConstituentKey = Check.NotNullOrWhiteSpace(
                originConstituentKey, nameof(originConstituentKey), DocumentConsts.MaxOriginConstituentKeyLength)
        };
        return document;
    }

    // --- Write methods (called by DocumentPipelineRunManager when a pipeline completes) ---

    internal void SetMarkdown(string markdown)
    {
        if (!string.IsNullOrEmpty(Markdown))
            throw new BusinessException(VaultExtractErrorCodes.Document.MarkdownIsImmutable);
        Markdown = string.IsNullOrEmpty(markdown) ? null : markdown;
    }

    internal void SetTitle(string? title)
    {
        if (!string.IsNullOrEmpty(Title))
            throw new BusinessException(VaultExtractErrorCodes.Document.TitleIsImmutable);

        if (string.IsNullOrWhiteSpace(title))
        {
            Title = null;
            return;
        }

        // Title is LLM output and can be indirectly controlled through document content: collapse control characters
        // (including \r \n \t / null byte) to spaces, merge consecutive whitespace into one space, then Trim + truncate.
        // This mirrors FieldDefinition.NormalizeDisplayName, including dropping a trailing unpaired high surrogate after truncation
        // so JSON serialization / DB round-trips are not broken by a split surrogate pair.
        var cleaned = new string(title.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length > DocumentConsts.MaxTitleLength)
        {
            cleaned = cleaned[..DocumentConsts.MaxTitleLength];
            if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.Trim();
        }

        Title = cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Writes the language detected by OCR / extraction (#210: ending the former write-never dead field).
    /// Candidate values are trimmed and then allow-list validated by <see cref="LanguageTagValidator"/>. This value is exposed as a raw value
    /// outside PromptBoundary in MCP resource metadata headers, so the allow-list is a contract-level injection defense, sharing the same principle as
    /// DocumentType.TypeCodePattern: "the allow-list is the injection boundary". Empty, whitespace, or non-matching inputs do <b>not</b> overwrite
    /// existing values; they are discarded as "language not detected".
    /// Called by <see cref="Pipelines.DocumentPipelineRunManager.CompleteParseAsync"/> when text extraction completes.
    /// </summary>
    internal void SetLanguage(string? language)
    {
        var normalized = LanguageTagValidator.Normalize(language);
        if (normalized == null)
        {
            return;
        }

        Language = normalized;
    }

    /// <summary>
    /// Writes text extraction provenance (#210): a Domain typed metadata value object (provider name + archived manifest).
    /// Called by <see cref="Pipelines.DocumentPipelineRunManager.CompleteParseAsync"/>,
    /// and written atomically in the same transaction as <see cref="SetMarkdown"/>. The Markdown write-once invariant naturally makes this write-once too.
    /// </summary>
    internal void SetExtractionMetadata(DocumentParseMetadata? extractionMetadata)
    {
        ExtractionMetadata = extractionMetadata;
    }

    /// <summary>
    /// Reassigns / assigns the document's cabinet (#257). <c>null</c> = remove from cabinet (uncategorized).
    /// CabinetId is an orthogonal organization dimension. Reassignment <b>does not</b> trigger any pipeline or domain event; it is an atomic state change
    /// (same category as <see cref="SetFields"/>, called directly by the Application layer without a DomainService).
    /// The Application layer validates target cabinet existence and current-layer ownership (<c>DocumentAppService.UpdateCabinetAsync</c>).
    /// </summary>
    public void SetCabinet(Guid? cabinetId)
    {
        CabinetId = cabinetId;
    }

    /// <summary>
    /// Moves the document back to "uncategorized" when a cabinet is deleted (#194): semantic alias for calling <see cref="SetCabinet"/> with <c>null</c>.
    /// Called by <c>CabinetAppService.DeleteAsync</c> for all documents in the cabinet before deletion, avoiding dangling references to a deleted cabinet.
    /// </summary>
    public void UnassignCabinet()
    {
        SetCabinet(null);
    }

    /// <summary>
    /// Replaces the full set of type-bound field values (field architecture v2 / Issue #206 + #207). <c>FieldExtractionEventHandler</c> calls this after classification completes;
    /// operator edits (<c>UpdateExtractedFieldsAsync</c>) use the same path. Passing an empty collection clears all field rows.
    /// Callers submit the current full field value set for the document, after validating that value types match <see cref="DocumentFieldValue.DataType"/>
    /// and each <see cref="DocumentFieldValue.FieldDefinitionId"/> resolves from a <c>FieldDefinition</c> in the document's layer / type.
    /// <para>
    /// Uses <b>reconcile</b> rather than clear+add: rows for the same field value (by <see cref="DocumentFieldValue.FieldDefinitionId"/> +
    /// <see cref="DocumentFieldValue.Order"/>, #212) are <b>updated in place</b>, missing rows are deleted, and new rows are inserted. Reason: under the composite key
    /// <c>(DocumentId, FieldDefinitionId, Order)</c>, clear+add creates delete+insert for the same key within one SaveChanges,
    /// risking unique conflicts / EF operation ordering issues. Changing <c>amount=100</c> to <c>200</c> is a same-field same-Order replacement;
    /// changing multi-value text <c>["a","b","c"] -> ["x","y"]</c> updates Order 0/1 in place and deletes Order 2 without key collision.
    /// </para>
    /// Atomic state change without DomainService mediation, unlike internal setters such as <see cref="SetMarkdown"/> that must be composed with pipeline completion transactions.
    /// <b>Precondition</b>: <see cref="DocumentTypeId"/> is not null because fields hang off document types; both caller paths run after classification completes.
    /// </summary>
    public void SetFields(IEnumerable<DocumentFieldValue>? values)
    {
        var incoming = values?.ToList() ?? new List<DocumentFieldValue>();

        _extractedFieldValues.RemoveAll(existing =>
            incoming.All(v => v.FieldDefinitionId != existing.FieldDefinitionId || v.Order != existing.Order));

        foreach (var value in incoming)
        {
            var existing = _extractedFieldValues.FirstOrDefault(
                f => f.FieldDefinitionId == value.FieldDefinitionId && f.Order == value.Order);
            if (existing != null)
            {
                existing.SetValue(value);
            }
            else
            {
                _extractedFieldValues.Add(new DocumentExtractedField(Id, TenantId, value));
            }
        }
    }

    /// <summary>
    /// Bitwise set / clear for one review reason (#284): the <b>only</b> entry point for writing reasons. Each bit is maintained by exactly one phase
    /// (UnresolvedClassification <- classification phase, inline in this class; MissingRequiredFields <- field extraction phase, called by the Application-layer
    /// handler / appservice after evaluation in the same UoW as field writes). Bitwise operations ensure the two phases do not overwrite each other.
    /// The aggregate root does not expose a whole-value setter, preventing one phase from accidentally overwriting another phase's decision.
    /// <c>public</c> is required because the MRF write point lives in the Application layer across assemblies; visibility is broader, but the "one bit, one phase" rule still applies.
    /// </summary>
    public void SetReviewReason(DocumentReviewReasons reason, bool present)
    {
        ReviewReasons = present ? (ReviewReasons | reason) : (ReviewReasons & ~reason);
    }

    /// <summary>
    /// Writes the duplicate-detection fingerprint (#411): the SHA-256 of this type's normalized unique-key field
    /// values, computed by the field extraction stage after <see cref="SetFields"/>. <c>null</c> / whitespace clears
    /// it (no unique-key fields configured, or a partial key). Unlike <see cref="SetMarkdown"/> this is <b>not</b>
    /// write-once: the fingerprint is derived from <see cref="ExtractedFieldValues"/> and must track every
    /// re-extraction. <c>public</c> because the compute point lives in the Application layer (<c>FieldExtractionService</c>),
    /// the same cross-assembly reason as <see cref="SetReviewReason"/>.
    /// </summary>
    public void SetFieldFingerprint(string? fieldFingerprint)
    {
        FieldFingerprint = string.IsNullOrWhiteSpace(fieldFingerprint)
            ? null
            : Check.Length(fieldFingerprint, nameof(fieldFingerprint), DocumentConsts.MaxFieldFingerprintLength);
    }

    /// <summary>
    /// Records the operator's "not a duplicate / acceptable re-upload" decision (#411): clears the
    /// <see cref="DocumentReviewReasons.DuplicateSuspected"/> reason and sets <see cref="DuplicateAllowed"/> so a later
    /// re-extraction does not re-raise it. Lifecycle re-derivation (which may now release the document to Ready) is
    /// done by the Application caller through <c>DocumentPipelineRunManager</c>. <c>public</c> for the same
    /// cross-assembly reason as <see cref="SetReviewReason"/>.
    /// </summary>
    public void AllowDuplicate()
    {
        DuplicateAllowed = true;
        SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: false);
    }

    // High-confidence path: classification is decided -> clear UnresolvedClassification and reset disposition to NotReviewed.
    internal void ApplyAutomaticClassificationResult(
        Guid documentTypeId,
        double classificationConfidence)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = Check.Range(classificationConfidence, nameof(classificationConfidence), 0d, 1d);
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: false);
        // #346: a concrete type is now assigned, so this is no longer a container; clear the marker (and the stale
        // segmentation-incomplete signal) to avoid the contradictory "has a type AND is a container" state.
        // #377/#379: routing the flag through SetContainerFlag clears the stale IsSegmented resume marker on the
        // container->concrete transition (single choke point — see SetContainerFlag), so the now-concrete document's
        // own embedded-document routing can run when re-segmented instead of being skipped. #378 was exactly this
        // omission on the automatic path (reachable via RerecognizeAsync) while only the operator path cleared it.
        // #349: on a true->false transition raise ContainerMarkerClearedEvent so the in-process handler retracts any
        // already-spawned sub-documents (soft-delete + DocumentDeletedEto) and removes the container's segment rows.
        var wasContainer = IsContainer;
        SetContainerFlag(false);
        SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: false);
        // #411: a (re)classification is a fresh duplicate-review context; the cascade re-extraction recomputes the fingerprint.
        ResetDuplicateDetectionState();
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null; // #284 review-fix: leaving Rejected disposition -> clear stale rejection reason; only Rejected should have one.
        if (wasContainer)
        {
            AddLocalEvent(new ContainerMarkerClearedEvent(Id));
        }
    }

    /// <summary>
    /// Marks classification as unresolved (waiting for operator-confirmed type): retracts the unconfirmed classification result and sets
    /// <see cref="DocumentReviewReasons.UnresolvedClassification"/> (blocking), preventing stale values from polluting external read models.
    /// <para>
    /// Invariant: "no confirmed type implies no type-bound field values". Once the type is retracted (<see cref="DocumentTypeId"/> = null),
    /// old <see cref="ExtractedFieldValues"/> no longer belong to any confirmed type and must be cleared together. Otherwise export DTO / MCP /
    /// export paths would expose a dirty model with fields but no type (#267 first exposed this when automatic reclassification fell to low confidence).
    /// Re-confirming a type (<see cref="ConfirmClassification"/> or high-confidence reclassification -> <c>DocumentClassifiedEto</c> -> field re-extraction) will restore fields.
    /// Centralizing this invariant in the aggregate root avoids per-read-path type filtering and special-case buildup.
    /// Also clears <see cref="DocumentReviewReasons.MissingRequiredFields"/> because without a type, required-field status cannot be evaluated.
    /// </para>
    /// </summary>
    internal void RequestClassificationReview()
    {
        DocumentTypeId = null;
        ClassificationConfidence = 0;
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: true);
        SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
        // #411: no type -> no fields -> no fingerprint basis; clear duplicate-detection state alongside the fields.
        ResetDuplicateDetectionState();
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null; // #284 review-fix: leaving Rejected disposition -> clear stale rejection reason.
        _extractedFieldValues.Clear();
    }

    /// <summary>
    /// Marks this document as a <b>container</b> (#346): a parent whose content is several independent documents
    /// (a multi-type bundle, or multiple instances of one type), so it runs <b>no</b> type-bound field extraction
    /// itself — each constituent is delegated to a sub-document. Detected at classification
    /// (<c>ClassificationResponse.IsContainer</c>), where the marker dominates the incidental type guess.
    /// <para>
    /// Unlike <see cref="RequestClassificationReview"/>, a container is a <b>correct</b> outcome, not an error: it
    /// does <b>not</b> set <see cref="DocumentReviewReasons.UnresolvedClassification"/>, so it never enters the
    /// operator review queue, and — with both key pipelines succeeded and no blocking reason — derives straight to
    /// <c>Ready</c> (Design A). <see cref="DocumentTypeId"/> stays null, confidence is reset to 0, and any existing
    /// field values are cleared (a container holds no single type's fields). <see cref="Markdown"/> /
    /// <see cref="Title"/> are kept as the original-file / provenance anchor; only type-bound extraction is
    /// suppressed. The classification caller deliberately does <b>not</b> publish <c>DocumentClassifiedEto</c> for a
    /// container, so <c>FieldExtractionEventHandler</c> never cascades.
    /// </para>
    /// </summary>
    internal void MarkAsContainer()
    {
        // #355: capture the prior state before clearing it. A false→true transition where the document previously
        // had a concrete type means a re-recognition turned an already-classified document (downstream may have
        // built a record from its DocumentClassifiedEto / DocumentReadyEto) into a container — that record is now
        // invalid and downstream must be told to retract it. A fresh upload first detected as a container had no
        // prior type and no downstream record, so it raises nothing.
        var wasContainer = IsContainer;
        var hadConcreteType = DocumentTypeId.HasValue;

        // #377/#379: SetContainerFlag clears IsSegmented on the concrete->container transition (single choke point —
        // see SetContainerFlag), so any prior segmentation completion (an embedded-document run that already routed a
        // figure before this re-recognition) does NOT count as the container split having run, and the container split
        // runs exactly once now. A document that merely STAYS a container (wasContainer true) is a no-op here and
        // keeps its marker, so it is not re-split.
        SetContainerFlag(true);
        DocumentTypeId = null;
        ClassificationConfidence = 0;
        // A container is a correct outcome: clear the classification / field review reasons rather than setting them,
        // so it is not routed to the operator review queue and is not blocked from deriving to Ready. The
        // segmentation-incomplete signal (#346) is cleared too — a freshly detected container has not failed yet.
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: false);
        SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
        SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: false);
        // #411: a container holds no single type's fields, so it has no duplicate fingerprint.
        ResetDuplicateDetectionState();
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null;
        _extractedFieldValues.Clear();

        // #355: mirror of the container→type retraction (#349 ContainerMarkerClearedEvent). The in-process handler
        // publishes DocumentReclassifiedToContainerEto so downstream retracts the record derived from the former type.
        if (!wasContainer && hadConcreteType)
        {
            AddLocalEvent(new ContainerMarkerSetEvent(Id, TenantId));
        }
    }

    /// <summary>
    /// Records that the unified sub-document detection pass (#371) reached a terminal SUCCESS for the current
    /// recognition (constituents split + persisted, or confirmed nothing standalone to route). Sets the precise
    /// resume gate <see cref="IsSegmented"/> so the LLM split is not re-paid on a retry / re-enqueue (#372/#377).
    /// Called in the same transaction as the segment rows (from the Application-layer segmentation job, so public like
    /// <see cref="SetReviewReason"/>). Idempotent.
    /// </summary>
    public void MarkSegmented()
    {
        IsSegmented = true;
    }

    /// <summary>
    /// The single mutator of <see cref="IsContainer"/> (#378/#379 hardening): every container↔concrete transition
    /// flows through here so the coupled <see cref="IsSegmented"/> invariant cannot leak. Any <b>actual</b> change of
    /// the flag — either direction — invalidates a prior segmentation completion, because the container split and a
    /// concrete document's embedded-figure routing are different passes whose completion must not gate the other; so
    /// the resume marker is cleared on every transition. A no-op call (the flag is already at the requested value)
    /// leaves <see cref="IsSegmented"/> untouched, preserving a still-container's split marker and a still-concrete
    /// document's embedded-routing marker. Callers retain the direction-specific concerns (the
    /// <c>ContainerMarkerCleared</c>/<c>Set</c> events and review-reason resets), which depend on context they capture
    /// before calling this.
    /// <para>
    /// #378 was a silent-data-loss bug caused by one concrete-assigning path forgetting to clear
    /// <see cref="IsSegmented"/> while the other cleared it; funnelling the flag through one setter makes that class
    /// of omission unrepresentable. The aggregate-level transition-matrix test (<c>IsSegmentedTransitionMatrix_Tests</c>)
    /// asserts every reclassification path clears it.
    /// </para>
    /// </summary>
    private void SetContainerFlag(bool isContainer)
    {
        if (IsContainer == isContainer)
        {
            return;
        }

        IsContainer = isContainer;
        IsSegmented = false;
    }

    /// <summary>
    /// Resets duplicate-detection state (#411) on every (re)classification or type retraction. The
    /// <see cref="FieldFingerprint"/> is derived from one type's unique-key fields, the
    /// <see cref="DocumentReviewReasons.DuplicateSuspected"/> flag is recomputed by the next extraction, and the
    /// <see cref="DuplicateAllowed"/> operator override belongs to the prior type context — so all three are stale
    /// once the type changes. The concrete-type paths then re-extract and recompute the fingerprint; the retraction /
    /// container paths leave it null because the document holds no type's fields.
    /// </summary>
    private void ResetDuplicateDetectionState()
    {
        FieldFingerprint = null;
        DuplicateAllowed = false;
        SetReviewReason(DocumentReviewReasons.DuplicateSuspected, present: false);
    }

    internal void ConfirmClassification(Guid documentTypeId)
    {
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = 1.0;
        // Operator-confirmed type -> clear UC; MRF will be recomputed by subsequent field re-extraction. Clear it here first to avoid stale required-field decisions from the old schema.
        SetReviewReason(DocumentReviewReasons.UnresolvedClassification, present: false);
        SetReviewReason(DocumentReviewReasons.MissingRequiredFields, present: false);
        // #346: operator reclassifying a container to a concrete type clears the container marker (reversibility)
        // and any stale segmentation-incomplete signal; subsequent DocumentClassifiedEto cascades field extraction.
        // #377/#379: SetContainerFlag clears the stale IsSegmented resume marker on the container->concrete transition
        // (single choke point — see SetContainerFlag), so the now-concrete document's own embedded-document routing can
        // run if it is later re-segmented instead of being skipped by a stale marker.
        // #349: on a true->false transition raise ContainerMarkerClearedEvent so the in-process handler retracts any
        // already-spawned sub-documents (soft-delete + DocumentDeletedEto) and removes the container's segment rows.
        var wasContainer = IsContainer;
        SetContainerFlag(false);
        SetReviewReason(DocumentReviewReasons.SegmentationIncomplete, present: false);
        // #411: operator (re)confirmed a type; the cascade re-extraction recomputes the fingerprint for the new context.
        ResetDuplicateDetectionState();
        ReviewDisposition = DocumentReviewDisposition.Confirmed;
        RejectionReason = null; // #284 review-fix: rejection is recoverable; clear stale rejection reason after Reclassify / Confirm.
        if (wasContainer)
        {
            AddLocalEvent(new ContainerMarkerClearedEvent(Id));
        }
    }

    /// <summary>
    /// Operator rejects review (#284: reason is <b>required</b>): sets <see cref="ReviewDisposition"/> to Rejected as the authoritative rejection signal,
    /// writes <see cref="RejectionReason"/>, and moves <see cref="LifecycleStatus"/> to Failed as the coarse "unavailable" appearance.
    /// Keeps original file, Markdown, confidence, field values, and review reasons (<see cref="ReviewReasons"/> is unchanged).
    /// <para>
    /// <b>Rejection is recoverable, not terminal</b> (#237): this method only records the fact that the operator rejected it now; it does not seal the document.
    /// The operator may later Reclassify the same document to assign a type. At that point <see cref="ConfirmClassification"/> moves ReviewDisposition back to Confirmed,
    /// pipeline derivation returns it to Ready, and <c>DocumentReadyEto</c> is published again. Downstream consumers absorb the re-delivery monotonically and idempotently by ETO <c>EventTime</c>
    /// (see CLAUDE.md delivery semantics). The "was rejected -> has been reviewed again" trail is carried by ABP entity audit logs, not by an absorbing state / Reopen state machine on the aggregate root.
    /// </para>
    /// <para>
    /// <b>Valid exception to lifecycle derivation rules</b>: normally <see cref="LifecycleStatus"/> is derived by
    /// <see cref="DocumentPipelineRunManager"/> from pipeline run state. Directly calling <see cref="TransitionLifecycle"/>
    /// to Failed here is a valid override from the manual-review axis. Failed uniformly means "coarsely unavailable"; the <b>reason</b> is explained orthogonally by detailed fields
    /// (pipeline run = technical failure; <see cref="ReviewDisposition"/> = Rejected = operator rejection).
    /// </para>
    /// </summary>
    public void RejectReview(string reason)
    {
        RejectionReason = Check.NotNullOrWhiteSpace(reason, nameof(reason), DocumentConsts.MaxRejectionReasonLength);
        ReviewDisposition = DocumentReviewDisposition.Rejected;
        TransitionLifecycle(DocumentLifecycleStatus.Failed);
    }

    /// <summary>
    /// Transitions <see cref="LifecycleStatus"/> and emits <see cref="DocumentLifecycleStatusChangedEvent"/> only when the state actually changes.
    /// <para>
    /// <b>The absence of a legal transition matrix is intentional</b> (#237 Finding B): except for the <c>old == new</c> short-circuit, any <c>(old, new)</c> transition is allowed,
    /// including <c>Failed -> Ready</c> (revival by Reclassify after rejection) and <c>Ready -> Processing -> Ready</c> (manual pipeline rerun for an already-ready document).
    /// Both derive Ready again and re-emit <c>DocumentReadyEto</c>. The channel layer does not block this; downstream consumers absorb it monotonically and idempotently by ETO <c>EventTime</c>
    /// (CLAUDE.md delivery semantics). Not adding a hard state machine to the gateway aggregate root is an intentional tradeoff: keep the channel simple and push idempotency downstream.
    /// </para>
    /// </summary>
    internal void TransitionLifecycle(DocumentLifecycleStatus newStatus)
    {
        if (LifecycleStatus == newStatus)
            return;

        var oldStatus = LifecycleStatus;
        LifecycleStatus = newStatus;
        AddLocalEvent(new DocumentLifecycleStatusChangedEvent(Id, oldStatus, newStatus));
    }

}
