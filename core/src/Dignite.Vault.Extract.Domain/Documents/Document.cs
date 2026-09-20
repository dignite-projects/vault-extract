using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Abp.FlexFields;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Documents.Pipelines;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents;

public class Document : FullAuditedAggregateRoot<Guid>, IMultiTenant, IHasFlexFields
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
    /// document type's <b>normalized unique-key field values</b> (the <c>Field.IsUniqueKey</c> set). Two
    /// documents in the same layer + same <see cref="DocumentTypeId"/> sharing this value are likely the same
    /// business entity (e.g. the same receipt scanned twice). Written by the field extraction stage via
    /// <see cref="SetFieldFingerprint"/>; <c>null</c> when the type declares no unique-key fields, or when not all of
    /// them have an extracted value (a partial key is not fingerprinted, to avoid false collisions). Derived from
    /// <see cref="FlexFields"/> and therefore recomputed on every re-extraction; cleared whenever the type
    /// is retracted or the document becomes a container.
    /// </summary>
    public virtual string? FieldFingerprint { get; private set; }

    /// <summary>
    /// Durable operator override (#411): the operator reviewed a <see cref="DocumentReviewReasons.DuplicateSuspected"/>
    /// flag and decided this document is <b>not</b> a duplicate (or is an acceptable re-upload). When true, the field
    /// extraction stage does <b>not</b> re-raise <c>DuplicateSuspected</c> on subsequent re-extractions (#289 bulk /
    /// manual reclassify), so the operator's decision survives. Reset to false whenever the document is
    /// (re)classified or its type is retracted — a new type context is a fresh duplicate-review decision — and
    /// (#651 §6) whenever <see cref="SetFieldFingerprint"/> writes a <b>different</b> key, because a verdict
    /// about the old key values applies to nothing once they change. Set by <c>AllowDuplicateAsync</c>.
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
    /// (reference-by-id, no navigation property, no FK cascade), and passive provenance at the DB level (#481).
    /// <para>
    /// It is nonetheless the derived document's <b>only</b> route to a source file: since #487 a sub-document
    /// carries no <see cref="FileOrigin"/> of its own, so a consumer follows this pointer to the parent and
    /// downloads the parent's blob. The source therefore outlives its children, an invariant the #508
    /// <c>DocumentAppService</c> delete guards enforce at the application layer (no FK, no DB cascade).
    /// </para>
    /// Exposed at the egress so downstream can follow it for provenance.
    /// </summary>
    public virtual Guid? OriginDocumentId { get; private set; }

    /// <summary>
    /// Content-derived stable key of the source constituent this document was derived from (#306 figure path /
    /// #346 born-digital path): the SHA-256 of the Markdown slice text. NOT bbox (which drifts, #210).
    /// <c>null</c> for normally-uploaded documents.
    /// <para>
    /// #481 moved spawn idempotency off this pair and onto the <c>DocumentSegment</c> ledger's unique
    /// <c>(SourceDocumentId, SegmentKey)</c> index, so nothing on <c>Document</c> enforces uniqueness over
    /// <c>(OriginDocumentId, OriginConstituentKey)</c> any more; <c>DocumentParseBackgroundJob</c> consumes this
    /// key to look up the seed slice, and <c>RestoreAsync</c> fail-closes on it (#485).
    /// </para>
    /// </summary>
    public virtual string? OriginConstituentKey { get; private set; }

    // --- Aggregate-internal field validation warnings collection (#527 §4) ---

    private readonly List<DocumentFieldValidationWarning> _fieldValidationWarnings = new();

    /// <summary>
    /// Type-bound field validation warnings (#527): one merged warning per field whose extracted value failed a
    /// validation rule declared in the field's prompt. The extracted value itself stays in
    /// <see cref="FlexFields"/>; this collection carries only the warning messages and is kept strictly out of
    /// field-value queries, search, export, and event payloads (#527 §11). Reconciled — and coupled to the blocking
    /// <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit — through <see cref="ReplaceFieldValidationWarnings"/>.
    /// </summary>
    public virtual IReadOnlyCollection<DocumentFieldValidationWarning> FieldValidationWarnings => _fieldValidationWarnings.AsReadOnly();

    // --- Field architecture v3 value bag (#558) ---

    /// <summary>
    /// Authoritative storage for type-bound field values, one entry per field, keyed by
    /// <see cref="Fields.Field.Name"/>, persisted as a single JSON column. The sole truth source for
    /// field-value queries and persistence — v2's <c>DocumentExtractedField</c> child-row collection was
    /// removed once the v3 data migration ran (#561, #593).
    /// <para>
    /// This is FlexFields' own dictionary, deliberately <b>not</b> ABP's <c>ExtraProperties</c>: kept
    /// isolated from that shared bag so no other module can collide with a tenant's field names. Note
    /// that it is not the untyped extension bag CLAUDE.md forbids on this aggregate either — every key in
    /// it is constrained by a real <see cref="Fields.Field"/> definition, which is exactly what the
    /// forbidden <c>Dictionary&lt;string, object&gt;</c> extension bag would not have been.
    /// </para>
    /// <para>
    /// The derived <c>DocumentFlexFieldIndex</c> table alongside it is never authoritative: every row in
    /// it is re-derivable from this bag, which is what lets a field's type or searchability change be
    /// repaired by a rebuild rather than a data migration.
    /// </para>
    /// </summary>
    public virtual FlexFieldDictionary FlexFields { get; protected set; } = new();

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
    /// <para>
    /// <b>#635: a sub-document inherits its origin's owner.</b> Segmentation runs in a background job where
    /// <c>ICurrentUser.Id</c> is null, so ABP's <c>AuditPropertySetter</c> writes nothing and, before this,
    /// derived documents had no creator at all — invisible to the person who uploaded the bundle they came out
    /// of. <paramref name="creatorId"/> is the origin's <see cref="Volo.Abp.Auditing.IHasCreationTime"/> sibling
    /// <c>CreatorId</c>, assigned here rather than left to the audit setter; that setter returns early when
    /// <c>CreatorId</c> already has a value, so the explicit value survives <c>SaveChanges</c>
    /// (pinned by <c>DerivedDocumentOwnership_Tests</c> against a real provider). A <c>null</c> origin creator
    /// stays null — a document nobody owns spawns sub-documents nobody owns.
    /// </para>
    /// </summary>
    public static Document CreateDerived(
        Guid id,
        Guid? tenantId,
        FileOrigin? fileOrigin,
        Guid originDocumentId,
        string originConstituentKey,
        Guid? creatorId)
    {
        var document = new Document(id, tenantId, fileOrigin)
        {
            OriginDocumentId = Check.NotDefaultOrNull<Guid>(originDocumentId, nameof(originDocumentId)),
            OriginConstituentKey = Check.NotNullOrWhiteSpace(
                originConstituentKey, nameof(originConstituentKey), DocumentConsts.MaxOriginConstituentKeyLength),
            // Assignable here and nowhere outside the aggregate: CreatorId's setter is protected on
            // CreationAuditedAggregateRoot, which is what keeps "who owns this" out of reach of the application
            // layer except through this one factory.
            CreatorId = creatorId
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

    /// <summary>
    /// Operator correction of already-extracted <see cref="Markdown"/> (#555): fixes a small OCR / parsing
    /// error the pipeline got wrong. This is a <b>separate</b> path from <see cref="SetMarkdown"/> and does
    /// not relax it — <see cref="SetMarkdown"/> stays write-once for pipeline completion exactly as before.
    /// <see cref="CorrectMarkdown"/> instead requires the opposite precondition: Markdown must <b>already</b>
    /// be set, because a document with nothing extracted yet has nothing to correct, so it throws
    /// <see cref="VaultExtractErrorCodes.Document.NotTextExtracted"/> rather than silently behaving like a
    /// first write.
    /// <para>
    /// Public and atomic, the same category as <see cref="SetCabinet"/> / <see cref="SetFlexFields"/> — no
    /// DomainService mediation needed. Overwrites only <see cref="Markdown"/> itself: <see cref="Title"/>,
    /// <see cref="Language"/>, <see cref="ExtractionMetadata"/>, and <see cref="FieldFingerprint"/> are left
    /// exactly as they were from the original extraction, because a manual text fix does not re-derive any
    /// of them the way a real pipeline re-run would. No length cap, unlike upload's provider-driven ceiling.
    /// </para>
    /// <para>
    /// No history table, no "PreviousMarkdown" snapshot column: the "was corrected, by whom, when, from
    /// what" trail is carried by ABP entity audit logging, the same reasoning already applied to
    /// <see cref="RejectReview"/> and <c>DocumentAppService.ResolveFieldValidationWarningsAsync</c>.
    /// </para>
    /// </summary>
    public void CorrectMarkdown(string markdown)
    {
        if (string.IsNullOrEmpty(Markdown))
            throw new BusinessException(VaultExtractErrorCodes.Document.NotTextExtracted);

        Markdown = Check.NotNullOrWhiteSpace(markdown, nameof(markdown));
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
        // This mirrors Fields.Field.NormalizeDisplayName, including dropping a trailing unpaired high surrogate after truncation
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
    /// (same category as <see cref="SetFlexFields"/>, called directly by the Application layer without a DomainService).
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
    /// Replaces the whole type-bound field value set. <c>FieldExtractionService</c> calls this after the
    /// classification cascade runs; operator edits (<c>UpdateExtractedFieldsAsync</c>) use the same path.
    /// <para>
    /// Whole-set replacement, not a merge: extraction produces the complete set for a document's type in
    /// one call, so a field absent from <paramref name="values"/> means "this document has no value for
    /// it", not "leave whatever was there". Merging would let a value from a previous type survive a
    /// reclassification.
    /// </para>
    /// <para>
    /// Keyed by <see cref="Fields.Field.Name"/>, because that is the bag's key. The caller resolves names
    /// from the field definitions and is responsible for having validated each value against its field
    /// type first — the aggregate stores what it is given.
    /// </para>
    /// Atomic state change without DomainService mediation, unlike internal setters such as
    /// <see cref="SetMarkdown"/> that must be composed with pipeline completion transactions.
    /// <b>Precondition</b>: <see cref="DocumentTypeId"/> is not null because fields hang off document types;
    /// both caller paths run after classification completes.
    /// </summary>
    public void SetFlexFields(IReadOnlyDictionary<string, object?>? values)
    {
        FlexFields.Clear();

        if (values == null)
        {
            return;
        }

        foreach (var pair in values)
        {
            // A null value is an absent field, not a stored null: keeping the key would make "extracted as
            // empty" and "never extracted" indistinguishable on the egress, and would put an entry in the
            // index for a value that does not exist.
            if (pair.Value != null)
            {
                FlexFields[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Replaces the full set of field validation warnings (#527 §4) and, in the <b>same operation</b>, sets or clears the
    /// blocking <see cref="DocumentReviewReasons.FieldValidationWarning"/> review reason so the collection and the bit can
    /// never diverge. <c>FieldExtractionService</c> calls this in the field-extraction write phase with the warnings for
    /// the document's current type; passing an empty collection (or <c>null</c>) clears all warnings and the bit — exactly
    /// what a later clean re-extraction does to release the document.
    /// <para>
    /// Reconciles like <see cref="SetFlexFields"/> (one row per field, keyed by
    /// <see cref="DocumentFieldValidationWarning.FieldDefinitionId"/>): existing rows are updated in place, dropped rows
    /// deleted, new rows inserted — avoiding a delete+insert on the same composite key within one <c>SaveChanges</c>.
    /// Callers submit warnings already resolved to the current field definitions (undeclared / stale-field warnings
    /// discarded) with normalized, length-bounded messages (#527 §3).
    /// </para>
    /// </summary>
    public void ReplaceFieldValidationWarnings(IEnumerable<FieldValidationWarning>? warnings)
    {
        var incoming = warnings?.ToList() ?? new List<FieldValidationWarning>();

        _fieldValidationWarnings.RemoveAll(existing =>
            incoming.All(w => w.FieldDefinitionId != existing.FieldDefinitionId));

        foreach (var warning in incoming)
        {
            var existing = _fieldValidationWarnings.FirstOrDefault(
                w => w.FieldDefinitionId == warning.FieldDefinitionId);
            if (existing != null)
            {
                existing.SetMessage(warning.Message);
            }
            else
            {
                _fieldValidationWarnings.Add(
                    new DocumentFieldValidationWarning(Id, TenantId, warning.FieldDefinitionId, warning.Message));
            }
        }

        // Couple the collection and the blocking review bit atomically: the reason is present iff a warning remains,
        // so a read path can never see a set bit with an empty collection (or the reverse).
        SetReviewReason(DocumentReviewReasons.FieldValidationWarning, _fieldValidationWarnings.Count > 0);
    }

    /// <summary>
    /// Resolves (removes) the field validation warnings for the given field definitions (#527 §9) — the operator's
    /// explicit "reviewed against the source" action — and re-couples the blocking
    /// <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit: it clears only when <b>no</b> warning remains, so
    /// resolving a subset leaves the document blocked on the rest. Ids without an active warning are ignored. The caller
    /// re-derives lifecycle afterwards so the document may transition to Ready once the bit is gone. Distinct from a
    /// manual field edit (<c>UpdateExtractedFieldsAsync</c>), which deliberately does <b>not</b> clear warnings.
    /// </summary>
    public void ResolveFieldValidationWarnings(IReadOnlyCollection<Guid> fieldDefinitionIds)
    {
        if (fieldDefinitionIds.Count == 0)
        {
            return;
        }

        var ids = fieldDefinitionIds as ISet<Guid> ?? new HashSet<Guid>(fieldDefinitionIds);
        _fieldValidationWarnings.RemoveAll(w => ids.Contains(w.FieldDefinitionId));
        SetReviewReason(DocumentReviewReasons.FieldValidationWarning, _fieldValidationWarnings.Count > 0);
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
    /// values, computed by the field extraction stage after <see cref="SetFlexFields"/>. <c>null</c> / whitespace clears
    /// it (no unique-key fields configured, or a partial key). Unlike <see cref="SetMarkdown"/> this is <b>not</b>
    /// write-once: the fingerprint is derived from <see cref="FlexFields"/> and must track every
    /// re-extraction. <c>public</c> because the compute point lives in the Application layer (<c>FieldExtractionService</c>),
    /// the same cross-assembly reason as <see cref="SetReviewReason"/>.
    /// <para>
    /// <b>A changed key withdraws <see cref="DuplicateAllowed"/></b> (#651 §6). The operator's "not a duplicate"
    /// verdict was about the key values that were there when they made it; once those change it applies to
    /// nothing, and left standing it would suppress detection forever for values nobody ever reviewed. The rule
    /// lives <b>here</b>, at the state transition, rather than at the call sites — there are four of them (the
    /// extraction write, extraction's no-definitions clear, the #528 duplicate-basis cleanup and the operator's
    /// manual correction) and a rule sorted into methods leaks the moment a fifth one is added. That includes
    /// the two that write <c>null</c>: a type that has lost its unique key has no key for a verdict to be about,
    /// the same reasoning <see cref="ResetDuplicateDetectionState"/> applies on a type change.
    /// </para>
    /// <para>
    /// "Changed" is an ordinal comparison of the <b>normalized</b> value, so <c>null</c> → <c>null</c> and
    /// equal → equal are no-ops and a re-extraction that reproduces the same key leaves the override intact —
    /// which is what makes the override survive routine re-extraction, as #411 intends. <c>null</c> ↔ non-null
    /// counts as a change in both directions.
    /// </para>
    /// <para>
    /// Returns whether the value changed, so the caller does not need a second comparison of its own to decide
    /// whether to re-run detection. Two comparisons of the same fact is how the entity and its caller drift.
    /// </para>
    /// </summary>
    public bool SetFieldFingerprint(string? fieldFingerprint)
    {
        var normalized = string.IsNullOrWhiteSpace(fieldFingerprint)
            ? null
            : Check.Length(fieldFingerprint, nameof(fieldFingerprint), DocumentConsts.MaxFieldFingerprintLength);

        if (string.Equals(FieldFingerprint, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        FieldFingerprint = normalized;
        DuplicateAllowed = false;
        return true;
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
        // #527 §7: reclassification to another type clears the previous type's validation warnings immediately.
        ClearFieldValidationWarnings();
        // #491: FieldExtractionIncomplete is deliberately NOT cleared here, unlike the duplicate state above. Markdown is
        // write-once, so oversized-for-the-old-type means oversized-for-the-new-type; and the cascade re-extraction is
        // queued only after this UoW commits, so at this instant the latest field-extraction run is still the prior
        // Succeeded decline. Clearing the bit would let DeriveLifecycleAsync derive a premature Ready in the window
        // before the new type's extraction runs. The re-extraction re-evaluates the bit and clears it if it now fits.
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
    /// old field values in the <see cref="FlexFields"/> bag no longer belong to any confirmed type and must be
    /// cleared. Otherwise export DTO / MCP /
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
        // #491: no type -> no field definitions -> no extraction call to decline; a stale FieldExtractionIncomplete would
        // otherwise pin a second blocking reason onto a document whose only real problem is the unresolved type.
        SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: false);
        // #411: no type -> no fields -> no fingerprint basis; clear duplicate-detection state alongside the fields.
        ResetDuplicateDetectionState();
        // #527 §7: becoming unclassified clears all type-bound validation warnings.
        ClearFieldValidationWarnings();
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null; // #284 review-fix: leaving Rejected disposition -> clear stale rejection reason.
        SetFlexFields(null);
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
    /// suppressed. The classification caller takes the container branch, which never schedules the cascade
    /// field-extraction run and (still) does <b>not</b> publish <c>DocumentClassifiedEto</c> for a container.
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
        // #491: a container runs no type-bound field extraction at all, so it can never be "extraction incomplete";
        // leaving the bit set would block a correctly-detected container from deriving to Ready.
        SetReviewReason(DocumentReviewReasons.FieldExtractionIncomplete, present: false);
        // #411: a container holds no single type's fields, so it has no duplicate fingerprint.
        ResetDuplicateDetectionState();
        // #527 §7: becoming a container clears all type-bound validation warnings.
        ClearFieldValidationWarnings();
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
        RejectionReason = null;
        SetFlexFields(null);

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

    /// <summary>
    /// Clears all field validation warnings and the coupled blocking
    /// <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit (#527 §7). Called on every type change —
    /// reclassification to another concrete type, becoming unclassified, or becoming a container — because a warning is
    /// tied to the type whose prompt produced it and is stale the moment the type changes. Mirrors
    /// <see cref="ResetDuplicateDetectionState"/>: a blocking review signal owned by the field stage is dropped
    /// immediately, and a later re-extraction against the new type repopulates it if warranted. The pending
    /// field-extraction run scheduled transactionally with reclassification (#527 §8) holds the Ready gate in the
    /// meantime, so clearing the bit here cannot open a premature-Ready window.
    /// </summary>
    private void ClearFieldValidationWarnings()
    {
        _fieldValidationWarnings.Clear();
        SetReviewReason(DocumentReviewReasons.FieldValidationWarning, present: false);
    }

    /// <summary>
    /// Declares the document type at upload time (#623): an operator-level confirmation applied to a freshly
    /// created document, before any pipeline has run. Semantically equivalent to <see cref="ConfirmClassification"/>
    /// (sets <see cref="DocumentTypeId"/>, pins <see cref="ClassificationConfidence"/> to 1.0, and marks
    /// <see cref="ReviewDisposition"/> Confirmed), but deliberately narrower: it must never touch container flags,
    /// review reasons, <see cref="FlexFields"/>, the duplicate-detection fingerprint, or raise any event, because at
    /// this point no pipeline has produced anything for those to react to yet. The Classification pipeline stage
    /// itself is completed later, by the Parse-cascade branch (<c>DocumentParseBackgroundJob</c>), which is what
    /// actually creates the <c>Classification</c> run and publishes <c>DocumentClassifiedEto</c> at the correct
    /// point in the stage sequence (#623 decision 2) — this method only stamps the declaration onto the aggregate
    /// so it survives the asynchronous gap between upload and Parse completion.
    /// <para>
    /// Guarded to pre-pipeline use only: throws <see cref="InvalidOperationException"/> if <see cref="DocumentTypeId"/>
    /// is already set (declaring twice is a caller bug — <c>UploadAsync</c> calls this exactly once) or if
    /// <see cref="Markdown"/> is already set (this method is defined to run strictly before Parse; once Markdown is
    /// written, the Parse-cascade branch — not this method — is the only path that may act on the declared type).
    /// Both are internal invariants reachable only through a programming error, not user-facing business rules, so
    /// they deliberately carry no <c>VaultExtractErrorCodes</c> entry: an error code is a frozen serialized string
    /// and would never legitimately reach a client.
    /// </para>
    /// </summary>
    internal void DeclareDocumentType(Guid documentTypeId)
    {
        if (DocumentTypeId.HasValue)
            throw new InvalidOperationException("DeclareDocumentType can only be called on a document that has no document type yet.");
        if (!string.IsNullOrEmpty(Markdown))
            throw new InvalidOperationException("DeclareDocumentType must run before text extraction has written Markdown.");

        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        ClassificationConfidence = 1.0;
        ReviewDisposition = DocumentReviewDisposition.Confirmed;
        RejectionReason = null;
    }

    /// <summary>
    /// Retracts an upload-time declared type that turned out to be stale by the time Parse completed (code review
    /// on #623, 2026-09-05): the declared <see cref="DocumentType"/> was deleted in the window between upload and
    /// Parse completion, so <c>DocumentParseBackgroundJob</c>'s completion cascade cannot honor it and falls back
    /// to automatic classification instead. Undoes exactly what <see cref="DeclareDocumentType"/> set —
    /// <see cref="DocumentTypeId"/> back to <c>null</c>, confidence back to 0, disposition back to
    /// <see cref="DocumentReviewDisposition.NotReviewed"/> — so the persisted row falls back to automatic
    /// classification's normal "not yet classified" starting state instead of staying Confirmed against a type
    /// that no longer resolves to anything. Deliberately sets no review reason: the caller is about to enqueue the
    /// ordinary classification job, exactly like an undeclared upload, not report a terminal failure.
    /// <para>
    /// Exposed publicly via <see cref="DocumentPipelineRunManager.RetractDeclaredType"/>, so the guard refuses
    /// anything that is not the <em>exact</em> upload-declared, never-classified signature <see cref="DeclareDocumentType"/>
    /// produces: a confirmed type at confidence 1.0 with no field fingerprint and no extracted field values yet.
    /// This is deliberately narrower than "has a type" -- it must never be usable to undo a real classification
    /// (automatic or operator-confirmed) that has already produced field values, because unlike
    /// <see cref="DeclareDocumentType"/>'s pre-pipeline call site this one runs after Parse, where a real
    /// classification could already exist.
    /// </para>
    /// </summary>
    internal void RetractDeclaredType()
    {
        if (!DocumentTypeId.HasValue
            || ReviewDisposition != DocumentReviewDisposition.Confirmed
            || ClassificationConfidence != 1.0
            || FieldFingerprint != null
            || FlexFields.Count != 0)
        {
            throw new InvalidOperationException(
                "RetractDeclaredType can only be called on a document that carries exactly the upload-declared, "
                + "never-classified signature: a confirmed type at confidence 1.0 with no field fingerprint and no "
                + "extracted field values.");
        }

        DocumentTypeId = null;
        ClassificationConfidence = 0;
        ReviewDisposition = DocumentReviewDisposition.NotReviewed;
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
        // #527 §7: reclassification to another type clears the previous type's validation warnings immediately.
        ClearFieldValidationWarnings();
        // #491: FieldExtractionIncomplete is deliberately NOT cleared here — same reasoning as
        // ApplyAutomaticClassificationResult. Confirming a type does not shrink the Markdown, and clearing the bit before
        // the cascade re-extraction runs would open a premature-Ready window.
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
