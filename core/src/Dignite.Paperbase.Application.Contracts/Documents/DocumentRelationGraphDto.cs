using System;
using System.Collections.Generic;

namespace Dignite.Paperbase.Documents;

public class DocumentRelationGraphDto
{
    public Guid RootDocumentId { get; set; }

    public List<DocumentRelationNodeDto> Nodes { get; set; } = new();

    public List<DocumentRelationEdgeDto> Edges { get; set; } = new();
}

public class DocumentRelationNodeDto
{
    public Guid DocumentId { get; set; }

    public string? Title { get; set; }

    public string? DocumentTypeCode { get; set; }

    public DocumentLifecycleStatus LifecycleStatus { get; set; }

    public DocumentReviewStatus ReviewStatus { get; set; }

    public int Distance { get; set; }
}

public class DocumentRelationEdgeDto
{
    public Guid Id { get; set; }

    public Guid SourceDocumentId { get; set; }

    public Guid TargetDocumentId { get; set; }

    public string Description { get; set; } = default!;

    public RelationSource Source { get; set; }
}
