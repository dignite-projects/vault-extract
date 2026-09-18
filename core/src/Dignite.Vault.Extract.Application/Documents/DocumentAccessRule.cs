using Dignite.Vault.Extract.Permissions;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>The whole rule table, as data</b> (#635 decision 2). One row per operation family: the module-wide
/// permission that admits every type of the caller's layer, the per-<c>DocumentType</c> resource permission that
/// admits exactly one type (<c>null</c> when the family has no per-type arm at all), and whether the document's
/// own uploader may perform it.
/// <para>
/// The rule the checker applies to every row is the same:
/// <c>entry (Documents) AND ( module-wide OR owner-where-allowed OR the grant on the subject's type )</c>.
/// Entry gates all three arms, for every rule <b>including the module-wide-only ones</b> — that is what turned
/// <c>PermanentDeleteAsync</c>, <c>RetryPipelineAsync</c>, the four <c>Reprocessing</c> methods, the export and
/// the untyped upload branch from <c>[Authorize]</c> attributes sitting outside the table into rows on it.
/// </para>
/// <para>
/// Rules are static readonly fields rather than an enum precisely so a call site reads
/// <c>CheckAsync(DocumentAccessRule.Edit, subject)</c> and cannot pair "edit" with the read permission, which is
/// what hand-writing the AND/OR at a dozen enforcement points invites.
/// </para>
/// <para>
/// <b>One document write is deliberately not a row here: the cabinet cascade.</b>
/// <c>CabinetAppService.DeleteAsync</c> clears <c>CabinetId</c> on every document of the cabinet it deletes,
/// recycle-bin ones included, and asserts only <b>entry</b> on top of its own <c>Cabinets.Delete</c>. Unassigning
/// on cabinet deletion is the cabinet's lifecycle, not an operation on any document: it exists so documents do
/// not dangle at a deleted row. Narrowing it by a read scope would be worse than leaving it — the documents the
/// caller could not reach would keep pointing at a cabinet that no longer exists, which is the exact state #530
/// removed. Entry is asserted so "may not open the documents area" still means "may not bulk-unfile documents in
/// it".
/// </para>
/// </summary>
/// <param name="ModuleWidePermission">
/// The standard permission that admits every type of the caller's layer, and untyped documents with it.
/// </param>
/// <param name="ResourcePermission">
/// The per-type grant that admits exactly the subject's type, or <c>null</c> for a family that has no per-type
/// arm: a <c>null</c> here is the statement "this operation is module-wide only, by decision", and it is stated
/// on the row rather than by the operation's absence from the table.
/// </param>
/// <param name="OwnerArm">
/// How far the document's own uploader gets without any grant (#635 decision 1). <c>Always</c> for Read /
/// Delete / Restore — an uploader must be able to see and withdraw their own work whatever state it is in.
/// <c>UnlessUnderReview</c> for Edit and Retry: both clear blocking review reasons as a <i>side effect</i>
/// (see <see cref="ReviewReasonPolicy.OwnerLocking"/>), so an owner is locked out of modifying a document that
/// is blocked on anything but its classification. <c>Never</c> for Review — its three methods exist to be the
/// second pair of eyes, and a suspected duplicate invoice is the adversarial case the review queue is for — for
/// DeclareType, and for every row with no per-type arm.
/// </param>
public sealed record DocumentAccessRule(
    string ModuleWidePermission,
    string? ResourcePermission,
    DocumentOwnerArm OwnerArm)
{
    /// <summary>
    /// Detail, blob, list / recycle-bin / export rows, pipeline runs, and the MCP paths that delegate to them.
    /// The owner arm is what makes an uploader able to confirm their own upload processed — the day-one failure
    /// #635 exists to fix.
    /// </summary>
    public static readonly DocumentAccessRule Read = new(
        VaultExtractPermissions.Documents.ReadAll,
        VaultExtractResourcePermissions.Read,
        OwnerArm: DocumentOwnerArm.Always);

    /// <summary>
    /// The operator edit family: confirm / reclassify (whose <b>target</b> type is judged separately by
    /// <see cref="DeclareType"/>), <c>UpdateMarkdownAsync</c>, <c>UpdateExtractedFieldsAsync</c>,
    /// <c>RerecognizeAsync</c>, <c>ReextractFieldsAsync</c>, and <c>UpdateCabinetAsync</c> — filing is a write
    /// (<c>SetCabinet</c> + <c>UpdateAsync</c>), so #635 moved it here off the Read rule, where a Read grant was
    /// no longer read-only.
    /// </summary>
    public static readonly DocumentAccessRule Edit = new(
        VaultExtractPermissions.Documents.ConfirmClassification,
        VaultExtractResourcePermissions.Edit,
        OwnerArm: DocumentOwnerArm.UnlessUnderReview);

    /// <summary>
    /// <c>AllowDuplicateAsync</c>, <c>ResolveFieldValidationWarningsAsync</c>, <c>RejectReviewAsync</c> — the same
    /// two permission names as <see cref="Edit"/>, and the <b>only</b> difference is that the owner arm is shut.
    /// <para>
    /// That difference is why ownership is a per-rule flag rather than one global arm on the checker. These three
    /// clear a blocking review reason: they are the channel's data-quality gate on the way to
    /// <c>DocumentReadyEto</c>, and a duplicate or a field-validation warning exists so that someone other than
    /// the uploader checks the uploader's work. Declaring a type at upload (#629) bypasses only the
    /// classification reason, never these, so it is no precedent.
    /// </para>
    /// </summary>
    public static readonly DocumentAccessRule Review = new(
        VaultExtractPermissions.Documents.ConfirmClassification,
        VaultExtractResourcePermissions.Edit,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>Soft delete (<c>DeleteAsync</c>). An uploader may withdraw a mistaken upload of their own.</summary>
    public static readonly DocumentAccessRule Delete = new(
        VaultExtractPermissions.Documents.Delete,
        VaultExtractResourcePermissions.Delete,
        OwnerArm: DocumentOwnerArm.Always);

    /// <summary>
    /// Restore from the recycle bin. <b>Whoever may delete may undo:</b> the per-type arm is deliberately
    /// <see cref="VaultExtractResourcePermissions.Delete"/> — the same grant <see cref="Delete"/> uses — rather
    /// than a fifth resource permission, and the owner arm follows for the same reason. Undoing an operation is
    /// not a wider right than the operation (#632).
    /// </summary>
    public static readonly DocumentAccessRule Restore = new(
        VaultExtractPermissions.Documents.Restore,
        VaultExtractResourcePermissions.Delete,
        OwnerArm: DocumentOwnerArm.Always);

    /// <summary>
    /// <c>RetryPipelineAsync</c>. #635 revisits #632's "module-wide only, by decision": retry is a single-document
    /// operator action on the detail page, the same act as <c>RerecognizeAsync</c> beside it, which already sits
    /// on the Edit arm — it has none of the reasons the other three module-wide-only rows have (irreversible,
    /// admin-level bulk, whole-layer aggregate). Left as it was, a caller holding a <c>Read</c> grant plus
    /// <c>Pipelines.Retry</c> re-ran OCR and classification on any readable document, around the per-type Edit
    /// gate. <c>Pipelines.Retry</c> keeps its meaning as the module-wide arm; the <c>Edit</c> grant and ownership
    /// become the per-type and own-document arms, the same shape as <see cref="Restore"/> reusing Delete's grant.
    /// </summary>
    public static readonly DocumentAccessRule Retry = new(
        VaultExtractPermissions.Documents.Pipelines.Retry,
        VaultExtractResourcePermissions.Edit,
        // UnlessUnderReview, like Edit: a retried field-extraction run replaces the whole validation-warning set
        // and recomputes the duplicate fingerprint from the new values, exactly as re-extraction does.
        OwnerArm: DocumentOwnerArm.UnlessUnderReview);

    /// <summary>
    /// Declaring / assigning a type: <c>UploadAsync</c>'s <c>DocumentTypeId</c> and the <b>target</b> type of
    /// Confirm / Reclassify. The #629 rule, unchanged — it is about the type being assigned, never about the
    /// document's current type, which is <see cref="Edit"/>'s job, and never about who owns anything (owning a
    /// document is not a licence to move it into a type the caller was never granted). An untyped subject reduces
    /// it to entry plus <c>ConfirmClassification</c>, which is exactly #629's untyped-upload rule, now with entry.
    /// </summary>
    public static readonly DocumentAccessRule DeclareType = new(
        VaultExtractPermissions.Documents.ConfirmClassification,
        VaultExtractResourcePermissions.Upload,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// <c>UploadAsync</c>'s admission, checked before <see cref="DeclareType"/>. Creating a document is not an
    /// operation on an existing one, so there is nothing to own and no type to grant against.
    /// </summary>
    public static readonly DocumentAccessRule Upload = new(
        VaultExtractPermissions.Documents.Upload,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// <c>PermanentDeleteAsync</c>. Module-wide only, by decision: it is irreversible, and it destroys the blob a
    /// restorable sub-document reaches through its provenance pointer. Ownership does not open it either — an
    /// uploader withdraws through <see cref="Delete"/>, which is recoverable.
    /// </summary>
    public static readonly DocumentAccessRule PermanentDelete = new(
        VaultExtractPermissions.Documents.PermanentDelete,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// <c>DocumentReprocessingAppService.PreviewFieldExtractionAsync</c> / <c>StartFieldExtractionAsync</c>.
    /// Module-wide only, by decision: admin-level bulk over a whole type.
    /// </summary>
    public static readonly DocumentAccessRule ReprocessFieldExtraction = new(
        VaultExtractPermissions.Documents.Reprocessing.FieldExtraction,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// <c>DocumentReprocessingAppService.PreviewReclassificationAsync</c> / <c>StartReclassificationAsync</c>.
    /// Module-wide only, by decision: admin-level bulk, cascading and destructive.
    /// </summary>
    public static readonly DocumentAccessRule ReprocessReclassification = new(
        VaultExtractPermissions.Documents.Reprocessing.Reclassification,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// <c>ExportAsync</c>'s admission. The rows inside are narrowed by the <see cref="Read"/> scope, so an
    /// uploader's own documents reach the file through that rule, not through this one.
    /// </summary>
    public static readonly DocumentAccessRule Export = new(
        VaultExtractPermissions.Documents.Export,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);

    /// <summary>
    /// The overview statistics (<c>DocumentStatisticsAppService.GetAsync</c>), which is also the source of the
    /// list page's review-queue badge. Module-wide only, by decision: these are whole-layer aggregates, and a
    /// whole-layer overview has no per-type — or per-uploader — meaning. Recomputing it inside one caller's scope
    /// would be a different statistic wearing the same name.
    /// </summary>
    public static readonly DocumentAccessRule Statistics = new(
        VaultExtractPermissions.Documents.ReadAll,
        ResourcePermission: null,
        OwnerArm: DocumentOwnerArm.Never);
}
