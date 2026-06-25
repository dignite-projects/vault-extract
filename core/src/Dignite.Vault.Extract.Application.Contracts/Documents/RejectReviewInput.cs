using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Vault.Extract.Documents;

public class RejectReviewInput
{
    /// <summary>Rejection reason (#284: <b>required</b>, independent from the removed ClassificationReason).</summary>
    [Required]
    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxRejectionReasonLength))]
    public string Reason { get; set; } = default!;
}
