using System;
using Dignite.Paperbase.Documents;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents;

public class DocumentRelationTests
{
    [Fact]
    public void Confirm_Should_Flip_Source_To_Manual()
    {
        var relation = new DocumentRelation(
            Guid.NewGuid(),
            tenantId: null,
            sourceDocumentId: Guid.NewGuid(),
            targetDocumentId: Guid.NewGuid(),
            description: "本文档引用了主合同的付款条款",
            source: RelationSource.AiSuggested);

        relation.Confirm();

        relation.Source.ShouldBe(RelationSource.Manual);
    }

    [Fact]
    public void Constructor_Should_Reject_Empty_Document_Id()
    {
        var exception = Should.Throw<BusinessException>(() => new DocumentRelation(
            Guid.NewGuid(),
            tenantId: null,
            sourceDocumentId: Guid.Empty,
            targetDocumentId: Guid.NewGuid(),
            description: "本文档引用了主合同的付款条款",
            source: RelationSource.AiSuggested));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentRelationDocumentIdRequired);
    }

    [Fact]
    public void Constructor_Should_Reject_Self_Relation()
    {
        var documentId = Guid.NewGuid();

        var exception = Should.Throw<BusinessException>(() => new DocumentRelation(
            Guid.NewGuid(),
            tenantId: null,
            sourceDocumentId: documentId,
            targetDocumentId: documentId,
            description: "本文档引用了主合同的付款条款",
            source: RelationSource.AiSuggested));

        exception.Code.ShouldBe(PaperbaseErrorCodes.DocumentRelationCannotTargetSelf);
    }

    [Fact]
    public void Constructor_Should_Reject_Blank_Description()
    {
        Should.Throw<ArgumentException>(() => new DocumentRelation(
            Guid.NewGuid(),
            tenantId: null,
            sourceDocumentId: Guid.NewGuid(),
            targetDocumentId: Guid.NewGuid(),
            description: " ",
            source: RelationSource.AiSuggested));
    }
}
