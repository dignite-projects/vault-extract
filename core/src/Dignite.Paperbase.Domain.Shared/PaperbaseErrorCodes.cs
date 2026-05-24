namespace Dignite.Paperbase;

/// <summary>
/// 错误码字符串：是 i18n 字典匹配 key + 下游 consumer 按 code 分支处理的 wire-level 协议契约。
/// 必须是 <c>const</c>——任何运行时改动都会让 Localization/Paperbase/*.json 的映射失效，
/// 并破坏下游消费方既有的 try/catch (code == "Paperbase:Xxx") 分支逻辑。
/// </summary>
public static class PaperbaseErrorCodes
{
    public const string MarkdownIsImmutable = "Paperbase:MarkdownIsImmutable";
    public const string TitleIsImmutable = "Paperbase:TitleIsImmutable";
    public const string InvalidDocumentTypeCode = "Paperbase:InvalidDocumentTypeCode";
    public const string InvalidDocumentTypeCodeFormat = "Paperbase:InvalidDocumentTypeCodeFormat";
    public const string DocumentTypeCodeAlreadyExists = "Paperbase:DocumentTypeCodeAlreadyExists";
    public const string DocumentTypeInUse = "Paperbase:DocumentTypeInUse";
    public const string DocumentTypeRestoreConflict = "Paperbase:DocumentTypeRestoreConflict";
    public const string InvalidDocumentTypeDisplayName = "Paperbase:InvalidDocumentTypeDisplayName";
    public const string NoDocumentTypesConfigured = "Paperbase:NoDocumentTypesConfigured";
    public const string FieldDefinitionAlreadyExists = "Paperbase:FieldDefinitionAlreadyExists";
    public const string InvalidFieldDefinitionName = "Paperbase:InvalidFieldDefinitionName";
    public const string InvalidFieldDefinitionDisplayName = "Paperbase:InvalidFieldDefinitionDisplayName";
    public const string FieldDefinitionRestoreConflict = "Paperbase:FieldDefinitionRestoreConflict";
    public const string FieldDefinitionParentTypeMissing = "Paperbase:FieldDefinitionParentTypeMissing";
    public const string DocumentDuplicate = "Paperbase:DocumentDuplicate";
    public const string DocumentInRecycleBin = "Paperbase:DocumentInRecycleBin";
    public const string PipelineNotRetryable = "Paperbase:PipelineNotRetryable";
    public const string PipelineRetryInProgress = "Paperbase:PipelineRetryInProgress";
    public const string PipelineNeverRan = "Paperbase:PipelineNeverRan";
    public const string UnknownPipelineCode = "Paperbase:UnknownPipelineCode";
    public const string DocumentNotClassified = "Paperbase:DocumentNotClassified";
    public const string UnknownExtractedField = "Paperbase:UnknownExtractedField";
    public const string InvalidExtractedFieldValue = "Paperbase:InvalidExtractedFieldValue";

    // 文件柜（#194）
    public const string InvalidCabinetDisplayName = "Paperbase:InvalidCabinetDisplayName";
    public const string CabinetDisplayNameAlreadyExists = "Paperbase:CabinetDisplayNameAlreadyExists";
    public const string InvalidCabinetId = "Paperbase:InvalidCabinetId";
}
