using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Vault.Extract.Documents.Cabinets;

public class CabinetDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
}
