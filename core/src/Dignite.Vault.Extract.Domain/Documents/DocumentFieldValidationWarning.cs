using System;
using Dignite.Vault.Extract.Documents.Fields;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Field validation warning row</b> (#527 §4): a child entity of the <see cref="Document"/> aggregate recording that
/// one type-bound field's extracted value failed a validation rule declared in the field's prompt (e.g. a
/// bank-statement balance-consistency check). The model-extracted value is <b>preserved</b> on
/// <see cref="DocumentExtractedField"/> so an operator can compare it with the source; this row carries only the
/// human-readable <see cref="Message"/>, kept strictly separate from field values, search indexes, exports, and event
/// payloads (#527 §11).
/// <para>
/// Composite primary key <c>(DocumentId, FieldDefinitionId)</c>: one merged warning per field. Internally associates the
/// field through the immutable <see cref="FieldDefinitionId"/> (#207) with no field-name / display-name snapshot; read
/// paths resolve the current name. Modified only through <see cref="Document"/> (no standalone repository); the coupled
/// blocking <see cref="DocumentReviewReasons.FieldValidationWarning"/> bit is maintained by
/// <see cref="Document.ReplaceFieldValidationWarnings"/> in the same operation, so the collection and the bit can never
/// diverge.
/// </para>
/// <para>
/// Isolation contract (mirrors <see cref="DocumentExtractedField"/>): implements <see cref="IMultiTenant"/> so ABP
/// appends tenant global filters when the child navigation is used; does <b>not</b> implement <c>ISoftDelete</c> —
/// hidden through the parent <see cref="Document"/> filter on soft delete, cascade-deleted on hard delete. It
/// participates in no field-value index and is never consulted by field-value search / filtering.
/// </para>
/// </summary>
public class DocumentFieldValidationWarning : Entity, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual Guid DocumentId { get; private set; }

    /// <summary><see cref="FieldDefinition"/>.Id this warning is attached to (immutable internal association / key, #207).</summary>
    public virtual Guid FieldDefinitionId { get; private set; }

    /// <summary>
    /// Concise, human-readable validation message, rendered as escaped text (never HTML) on the operator UI. Bounded by
    /// <see cref="DocumentFieldValidationWarningConsts.MaxMessageLength"/>; the App layer normalizes and safely truncates
    /// the untrusted model output before it reaches here (#527 §3).
    /// </summary>
    public virtual string Message { get; private set; } = default!;

    protected DocumentFieldValidationWarning()
    {
    }

    internal DocumentFieldValidationWarning(Guid documentId, Guid? tenantId, Guid fieldDefinitionId, string message)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        FieldDefinitionId = fieldDefinitionId;
        SetMessage(message);
    }

    /// <summary>
    /// Writes / updates the message in place, called during reconcile for the same field. The App layer already
    /// discarded blank messages and truncated overlong ones at a character boundary (#527 §3); this is the defensive
    /// domain invariant, failing loudly on a blank or over-ceiling message rather than persisting one.
    /// </summary>
    internal void SetMessage(string message)
    {
        Message = Check.NotNullOrWhiteSpace(
            message, nameof(message), DocumentFieldValidationWarningConsts.MaxMessageLength);
    }

    public override object[] GetKeys() => new object[] { DocumentId, FieldDefinitionId };
}
