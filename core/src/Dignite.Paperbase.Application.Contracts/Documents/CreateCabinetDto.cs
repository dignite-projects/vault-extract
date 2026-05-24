using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class CreateCabinetDto
{
    [Required]
    [DynamicStringLength(typeof(CabinetConsts), nameof(CabinetConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;
}
