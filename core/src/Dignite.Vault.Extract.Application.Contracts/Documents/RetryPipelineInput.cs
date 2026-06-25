using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents;

public class RetryPipelineInput
{
    [Required]
    [StringLength(64)]
    public string PipelineCode { get; set; } = default!;
}
