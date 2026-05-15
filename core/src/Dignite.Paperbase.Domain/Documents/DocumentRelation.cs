using System;
using Dignite.Paperbase.Documents;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 文档间关系。独立聚合根，不内嵌于 Document。
/// </summary>
public class DocumentRelation : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>来源文档 ID</summary>
    public virtual Guid SourceDocumentId { get; private set; }

    /// <summary>目标文档 ID</summary>
    public virtual Guid TargetDocumentId { get; private set; }

    /// <summary>用户可读的关系说明（如：本合同补充了主合同第 3 条付款条款的执行细节）</summary>
    public virtual string Description { get; private set; } = default!;

    /// <summary>关系来源</summary>
    public virtual RelationSource Source { get; private set; }

    protected DocumentRelation() { }

    public virtual void Confirm()
    {
        Source = RelationSource.Manual;
    }

    public DocumentRelation(
        Guid id,
        Guid? tenantId,
        Guid sourceDocumentId,
        Guid targetDocumentId,
        string description,
        RelationSource source)
        : base(id)
    {
        TenantId = tenantId;
        SourceDocumentId = ValidateDocumentId(sourceDocumentId, nameof(sourceDocumentId));
        TargetDocumentId = ValidateDocumentId(targetDocumentId, nameof(targetDocumentId));

        if (SourceDocumentId == TargetDocumentId)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentRelationCannotTargetSelf);
        }

        Description = Check.NotNullOrWhiteSpace(
            description,
            nameof(description),
            DocumentRelationConsts.MaxDescriptionLength);
        Source = source;
    }

    private static Guid ValidateDocumentId(Guid documentId, string parameterName)
    {
        if (documentId == Guid.Empty)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentRelationDocumentIdRequired)
                .WithData("ParameterName", parameterName);
        }

        return documentId;
    }
}
