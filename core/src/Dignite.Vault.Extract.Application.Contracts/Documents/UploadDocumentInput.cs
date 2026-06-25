using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Content;

namespace Dignite.Vault.Extract.Documents;

public class UploadDocumentInput
{
    [Required]
    public IRemoteStreamContent File { get; set; } = default!;

    /// <summary>
    /// Optional cabinet assignment, the manual organization dimension (#194). null means unclassified.
    /// On upload, this must validate against an existing cabinet in the current layer
    /// (<c>CurrentTenant.Id</c>). It is orthogonal to the pipeline.
    /// </summary>
    public Guid? CabinetId { get; set; }
}
