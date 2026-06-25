using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Input for an operator-initiated document classification correction.
/// <para>
/// Difference from <see cref="ConfirmClassificationInput"/>: Confirm represents the "confirm" action
/// for the UnresolvedClassification review state, while Reclassify represents "the operator believes
/// the classification is wrong and overrides it" in any state. Both ultimately set
/// <see cref="Document.ReviewDisposition"/> to Confirmed, but separate APIs keep auditing and permission
/// governance clearer.
/// </para>
/// </summary>
public class ReclassifyDocumentInput
{
    /// <summary>Immutable target document type id for the overridden classification (#207: an internal stable handle; TypeCode can be renamed by admins and is not used as a reference key).</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }
}
