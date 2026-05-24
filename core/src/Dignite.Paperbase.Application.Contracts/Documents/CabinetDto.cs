using System;
using Volo.Abp.Application.Dtos;

namespace Dignite.Paperbase.Documents;

public class CabinetDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public string DisplayName { get; set; } = default!;
}
