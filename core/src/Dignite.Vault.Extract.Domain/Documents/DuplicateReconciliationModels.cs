using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The grouping key of one duplicate-collision bucket (#651 §5 step 1), as
/// <see cref="IDocumentRepository.CountDuplicateCollisionsAsync"/> returns it:
/// <c>FieldFingerprint</c> alone under <c>DuplicateDetectionScope.Layer</c>, and
/// <c>(FieldFingerprint, CreatorId)</c> under <c>DuplicateDetectionScope.Uploader</c>.
/// <para>
/// A record, because the reconciliation job looks buckets up by value from a dictionary: under <c>Layer</c> the
/// job must build the key with <see cref="CreatorId"/> <c>null</c>, matching exactly how the repository grouped.
/// <c>null</c> is a real bucket, not an absent one — under <c>Uploader</c> every uploaderless document (all
/// historical rows, and every machine-identity upload) shares it, which is the intended reading of "a null
/// <c>CreatorId</c> matches only another null <c>CreatorId</c>".
/// </para>
/// </summary>
public sealed record DuplicateCollisionKey(string FieldFingerprint, Guid? CreatorId);

/// <summary>
/// One row of the reconciliation scan (#651 §5 step 2): the four narrow columns needed to decide whether a
/// document's persisted <c>DuplicateSuspected</c> bit still matches its type's current scope, plus the id to load
/// it by and <see cref="IsDeleted"/> to tell a live row from a recycle-bin one.
/// <para>
/// Deliberately <b>not</b> a <see cref="Document"/>: the naive shape loads every row of the type including
/// <c>Markdown</c>, and almost all of those loads write nothing back. Keeping the loaded set down to the documents
/// whose verdict actually changes is the whole point of scanning this projection first.
/// </para>
/// </summary>
public class DuplicateReconciliationRow
{
    public Guid Id { get; set; }

    /// <summary>Null when the type declares no unique key, or the document's key is partial — never a duplicate.</summary>
    public string? FieldFingerprint { get; set; }

    /// <summary>The ownership anchor (#635's owner arm), null for historical and machine-identity uploads.</summary>
    public Guid? CreatorId { get; set; }

    public DocumentReviewReasons ReviewReasons { get; set; }

    /// <summary>The operator's durable "not a duplicate" override; such rows are skipped entirely by reconciliation.</summary>
    public bool DuplicateAllowed { get; set; }

    /// <summary>
    /// Whether this is a recycle-bin row. In scope for the <b>write</b> (restoring a document must not bring back a
    /// flag derived from a retracted rule) but excluded from the collision <b>counts</b>, mirroring
    /// <see cref="IDocumentRepository.FindDuplicateCandidatesAsync"/>'s ambient soft-delete filter.
    /// </summary>
    public bool IsDeleted { get; set; }
}
