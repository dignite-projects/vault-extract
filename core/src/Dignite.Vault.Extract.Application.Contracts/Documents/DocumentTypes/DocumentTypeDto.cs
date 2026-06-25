using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.DocumentTypes;

public class DocumentTypeDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string TypeCode { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public string? Description { get; set; }
    public double ConfidenceThreshold { get; set; }
    public int Priority { get; set; }
}
