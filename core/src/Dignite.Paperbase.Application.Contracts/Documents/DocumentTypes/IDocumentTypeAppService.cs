using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Documents.DocumentTypes;

/// <summary>
/// 文档类型管理（字段架构 v2）。按所属层精确匹配单层、不做 Host ∪ Tenant union；
/// 两层都通过此 AppService CRUD 自管——不存在 seed contributor / Module 启动注册路径。
/// </summary>
public interface IDocumentTypeAppService : IApplicationService
{
    Task<List<DocumentTypeDto>> GetVisibleAsync();

    /// <summary>调用方所在层已软删除的文档类型列表（回收站视图）。</summary>
    Task<List<DocumentTypeDto>> GetDeletedAsync();

    Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input);

    Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// 恢复软删除的文档类型，并级联恢复同 (TenantId, TypeCode) 下随之被软删除的字段定义。
    /// 若同代码已有活跃记录则抛 <see cref="PaperbaseErrorCodes.DocumentType.RestoreConflict"/>；
    /// 个别字段恢复时与活跃字段冲突的会被跳过（防御性，正常流程下不会发生）。
    /// </summary>
    Task<DocumentTypeDto> RestoreAsync(Guid id);
}
