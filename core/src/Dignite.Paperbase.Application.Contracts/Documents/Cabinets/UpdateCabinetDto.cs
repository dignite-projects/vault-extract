using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Cabinets;

public class UpdateCabinetDto
{
    [Required]
    [DynamicStringLength(typeof(CabinetConsts), nameof(CabinetConsts.MaxNameLength))]
    public string Name { get; set; } = default!;

    [DynamicStringLength(typeof(CabinetConsts), nameof(CabinetConsts.MaxDescriptionLength))]
    public string? Description { get; set; }
}
