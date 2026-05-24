using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Content;

namespace Dignite.Paperbase.Documents;

public class UploadDocumentInput
{
    [Required]
    public IRemoteStreamContent File { get; set; } = default!;

    /// <summary>
    /// 可选文件柜归属（人工组织维度，#194）。null = 未归类。
    /// 上传时校验必须是当前层（CurrentTenant.Id）已存在的柜。正交于 pipeline。
    /// </summary>
    public Guid? CabinetId { get; set; }
}
