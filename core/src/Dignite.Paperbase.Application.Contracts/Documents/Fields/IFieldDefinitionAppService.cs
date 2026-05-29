using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段定义管理（字段架构 v2 统一 API）。按所属层精确匹配单层、不跨层混合；
/// 两层都通过此 AppService CRUD 自管——不存在 seed contributor / Module 启动注册路径。
/// </summary>
public interface IFieldDefinitionAppService : IApplicationService
{
    /// <summary>
    /// 当前租户层指定文档类型下的字段定义列表（不跨层）。
    /// <see cref="GetFieldDefinitionListInput.OnlyDeleted"/> 为 <c>false</c> 返回活跃字段（按 DisplayOrder），
    /// 为 <c>true</c> 返回回收站（已软删除）字段（按 DeletionTime 倒序）。
    /// </summary>
    Task<List<FieldDefinitionDto>> GetListAsync(GetFieldDefinitionListInput input);

    Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input);

    Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// 恢复单个软删除的字段定义。要求父 <see cref="DocumentType"/>（同 TenantId + TypeCode）存在且活跃；
    /// 父类型缺失或仍处于已删除状态时抛 <see cref="PaperbaseErrorCodes.FieldDefinition.ParentTypeMissing"/>；
    /// 同名活跃字段已存在则抛 <see cref="PaperbaseErrorCodes.FieldDefinition.RestoreConflict"/>。
    /// 批量恢复请走 <see cref="IDocumentTypeAppService.RestoreAsync"/> 的级联路径。
    /// </summary>
    Task<FieldDefinitionDto> RestoreAsync(Guid id);
}
