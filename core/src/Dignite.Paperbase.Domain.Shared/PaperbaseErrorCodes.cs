namespace Dignite.Paperbase;

public static class PaperbaseErrorCodes
{
    public const string MarkdownIsImmutable = "Paperbase:MarkdownIsImmutable";
    public const string TitleIsImmutable = "Paperbase:TitleIsImmutable";
    public const string DocumentRelationDocumentIdRequired = "Paperbase:DocumentRelationDocumentIdRequired";
    public const string DocumentRelationCannotTargetSelf = "Paperbase:DocumentRelationCannotTargetSelf";
    public const string InvalidDocumentTypeCode = "Paperbase:InvalidDocumentTypeCode";
    public const string DocumentDuplicate = "Paperbase:DocumentDuplicate";
    public const string DocumentInRecycleBin = "Paperbase:DocumentInRecycleBin";
    public const string DuplicateClientTurnId = "Paperbase:DuplicateClientTurnId";
    public const string PipelineNotRetryable = "Paperbase:PipelineNotRetryable";
    public const string PipelineRetryInProgress = "Paperbase:PipelineRetryInProgress";
    public const string PipelineNeverRan = "Paperbase:PipelineNeverRan";
    public const string UnknownPipelineCode = "Paperbase:UnknownPipelineCode";
}
