using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Review;

/// <summary>
/// 审核状态判定器（#284）——纯函数式 domain service，只算"必填缺失（MissingRequiredFields）"维度。
/// <para>
/// 无仓储依赖：输入两个字段定义 Id 集合、输出布尔，可单元测试无 mock。调用方（字段抽取 handler /
/// 操作员补录 AppService）在已加载 required / extracted 集合的同一 UoW 内调用，据结果
/// <see cref="Document.SetReviewReason"/>。
/// </para>
/// <para>
/// 分类维度（<see cref="DocumentReviewReasons.UnresolvedClassification"/>）由 <see cref="Document"/> 写入方法内联处理
/// （它是 <c>DocumentTypeId.HasValue</c> 的镜像，各方法已有 typeId 上下文），不进本判定器——避免无谓依赖与重复查询。
/// </para>
/// </summary>
public class ReviewStateEvaluator : ITransientDependency
{
    /// <summary>
    /// 是否存在必填缺失：某 <c>IsRequired</c> 字段定义未出现在"已抽到值"的字段集合里。
    /// </summary>
    /// <param name="requiredFieldDefinitionIds">该文档类型下 <c>FieldDefinition.IsRequired == true</c> 的定义 Id。</param>
    /// <param name="extractedFieldDefinitionIds">该文档已抽到值的 <c>FieldDefinitionId</c>（distinct）。</param>
    public virtual bool MissingRequiredFieldsPresent(
        IReadOnlyCollection<Guid> requiredFieldDefinitionIds,
        IReadOnlyCollection<Guid> extractedFieldDefinitionIds)
    {
        if (requiredFieldDefinitionIds.Count == 0)
        {
            return false;
        }

        var extracted = extractedFieldDefinitionIds as ISet<Guid> ?? extractedFieldDefinitionIds.ToHashSet();
        return requiredFieldDefinitionIds.Any(id => !extracted.Contains(id));
    }
}
