namespace Dignite.Vault.Extract.Documents.DocumentTypes;

/// <summary>
/// What counts as a duplicate for one document type (#651): the set a document's
/// <c>Document.FieldFingerprint</c> is compared against when duplicate detection (#411) decides
/// whether to raise the blocking <see cref="DocumentReviewReasons.DuplicateSuspected"/> reason.
/// <para>
/// It lives on <c>DocumentType</c> rather than on <c>Field</c> because a type's unique key is <b>one</b> key
/// composed of several <c>IsUniqueKey</c> fields and the fingerprint is computed per type — a key made of three
/// fields cannot carry three scopes. It is tenant business semantics, so it is not host configuration either.
/// </para>
/// <para>
/// <b>The integer values are a persisted and serialized contract</b> (the <c>DocumentTypes</c> column, the
/// document-type DTOs, and <c>DocumentTypePackDto</c>), frozen once shipped, the same discipline the
/// <c>Extract:*</c> error codes follow. A third grouping — by cabinet, department, or anything else — would be a
/// new member here, never a second axis beside it.
/// </para>
/// <para>
/// This is <b>detection</b> scope, orthogonal to the <c>DocumentAccessScope</c> read scope #635 put on
/// <c>IDocumentRepository.FindDuplicateCandidatesAsync</c>: detection scope answers "what counts as a duplicate",
/// read scope answers "whose names may I show you".
/// </para>
/// </summary>
public enum DuplicateDetectionScope
{
    /// <summary>
    /// The default, and what every type did before #651: a fingerprint collides with any other document of the
    /// same layer + type, whoever uploaded it. Value <c>0</c>, so existing rows keep today's behaviour and no
    /// persisted state changes meaning on upgrade.
    /// </summary>
    Layer = 0,

    /// <summary>
    /// A fingerprint collides only with another document of the same layer + type that has the <b>same</b>
    /// <c>Document.CreatorId</c> — "this key is unique per uploader". For a tenant where several
    /// departments each keep their own copy of the same vendor invoice, the second upload is legitimate.
    /// <para>
    /// <b>A null <c>CreatorId</c> matches only another null <c>CreatorId</c></b> — equality on the anchor like any
    /// other value, with no fallback to layer-wide. Falling back would apply the stricter rule to exactly the rows
    /// nobody asked to be stricter about. Note who those rows are: not only historical data, but <b>every
    /// machine-identity upload</b> — <c>CreatorId</c> comes from <c>CurrentUser.Id</c>, which a
    /// <c>client_credentials</c> caller does not have — so under this scope all machine-ingested documents of a
    /// type form one bucket. That is intended, not a defect.
    /// </para>
    /// </summary>
    Uploader = 1
}
