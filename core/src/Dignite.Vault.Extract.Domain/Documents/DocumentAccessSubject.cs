using System;
using Dignite.Vault.Extract.Documents.DocumentTypes;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// What an authorization question is asked <b>about</b> (#635 decision 4): the two facts every rule in
/// <c>DocumentAccessRule</c> can consult — the type the operation is judged against, and who owns the document.
/// <para>
/// It is deliberately <b>not</b> a <see cref="Document"/>. Two of the three shapes have no document in hand: a
/// reclassification's <b>target</b> type is a type with no owner, and an untyped upload is neither. Modelling all
/// three as one record is what let the checker collapse from five call shapes to two — the previous
/// <c>CheckTargetTypeAsync</c> existed only because "a type, no document" had no way to be spelled.
/// </para>
/// <para>
/// It lives in the Domain beside <see cref="DocumentAccessScope"/>, which has to be here for
/// <see cref="DocumentQueries.ApplyMetadataFilter"/>, and carries no permission name of its own: a subject is a
/// pair of ids, and which rule reads which id is the Application layer's business.
/// </para>
/// </summary>
/// <param name="DocumentTypeId">
/// The type the operation is judged against: a document's <b>current</b> type for Read / Edit / Review / Delete /
/// Restore / Retry, the <b>target</b> type for DeclareType. <c>null</c> is an untyped document (unclassified,
/// failed classification, container) or an upload that declares no type — no grant can name it, so only the
/// module-wide arm (and, where the rule allows it, ownership) can reach it.
/// </param>
/// <param name="CreatorId">
/// Who uploaded the document — ABP's <see cref="Volo.Abp.Auditing.IHasCreationTime"/> sibling
/// <c>CreatorId</c>, set at insert from <c>ICurrentUser.Id</c>. <c>null</c> for a document created without a
/// principal (a pre-#635 derived sub-document, a seeded row) and for every shape that is not a document at all.
/// A <c>null</c> here never matches the ownership arm, whatever the caller's own id is.
/// </param>
/// <param name="UnderReview">
/// Whether this document's review state closes the ownership arm of the edit family —
/// <see cref="ReviewReasonPolicy.LocksOwnerEdits"/>, i.e. it carries a blocking reason other than
/// <see cref="DocumentReviewReasons.UnresolvedClassification"/>. Computed from the document; <c>false</c> for
/// the two shapes that are not a document (<see cref="OfType(Guid)"/>, <see cref="None"/>), where there is no
/// review state and no owner to lock out either.
/// </param>
public sealed record DocumentAccessSubject(Guid? DocumentTypeId, Guid? CreatorId, bool UnderReview)
{
    /// <summary>
    /// No document and no type: <c>UploadAsync</c>'s admission and its untyped branch, and every rule whose
    /// per-type arm is <c>null</c> anyway (PermanentDelete / Reprocessing / Export / Statistics). Both arms that
    /// need a subject fact are absent, so such a rule reduces to entry plus its module-wide permission.
    /// </summary>
    public static readonly DocumentAccessSubject None = new(null, null, UnderReview: false);

    /// <summary>A loaded document: its current type, its owner, and whether its review state locks that owner out.</summary>
    public static DocumentAccessSubject Of(Document document)
        => new(
            document.DocumentTypeId,
            document.CreatorId,
            ReviewReasonPolicy.LocksOwnerEdits(document.ReviewReasons));

    /// <summary>
    /// A <b>target</b> type being declared or assigned — the shape that replaced <c>CheckTargetTypeAsync</c>.
    /// There is no owner: nobody owns a type, and the document the type is being put on is judged separately by
    /// its own rule.
    /// </summary>
    public static DocumentAccessSubject OfType(DocumentType documentType)
        => new(documentType.Id, null, UnderReview: false);

    /// <summary>The same, from a type id the caller has already resolved under the ambient multi-tenancy filter.</summary>
    public static DocumentAccessSubject OfType(Guid documentTypeId)
        => new(documentTypeId, null, UnderReview: false);
}
