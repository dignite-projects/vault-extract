using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents;

public class ConfirmClassificationInput
{
    /// <summary>Immutable target document type id for the confirmed classification (#207: an internal stable handle; TypeCode can be renamed by admins and is not used as a reference key).</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }
}
