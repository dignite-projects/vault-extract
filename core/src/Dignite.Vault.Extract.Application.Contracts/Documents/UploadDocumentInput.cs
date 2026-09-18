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

    /// <summary>
    /// Optional declared document type (#623): the caller already knows the type (a business-system downstream
    /// submitting a known form, or an MCP ingest caller that was told what it is). Supplying this is equivalent
    /// to an operator calling <c>ConfirmClassificationAsync</c> on the document — <see cref="DocumentTypeId"/> is
    /// set, classification confidence is pinned to 1.0, <c>ReviewDisposition</c> becomes Confirmed, and no
    /// classification LLM call is made. On upload, this must resolve to an existing document type in the current
    /// layer (<c>CurrentTenant.Id</c>), the same exact single-layer matching as every other document-type lookup.
    /// <para>
    /// The upload right is judged once, against this type (#645): the module-wide
    /// <c>VaultExtractPermissions.Documents.Upload</c>, which uploads into every type of the caller's layer, or a
    /// resource grant of <c>VaultExtractResourcePermissions.Upload</c> on this specific type, which is sufficient
    /// on its own.
    /// </para>
    /// <para>
    /// <b>Leaving this null needs the module-wide <c>Documents.Upload</c></b>: an untyped upload hands the type
    /// decision to LLM classification, which may land the document in any type, so a caller whose upload right is
    /// per type must name the type (#629 decision 2).
    /// </para>
    /// </summary>
    public Guid? DocumentTypeId { get; set; }
}
