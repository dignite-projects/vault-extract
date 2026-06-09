using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class RejectReviewInput
{
    /// <summary>拒绝理由（#284：<b>必填</b>，独立于已删除的 ClassificationReason）。</summary>
    [Required]
    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxRejectionReasonLength))]
    public string Reason { get; set; } = default!;
}
