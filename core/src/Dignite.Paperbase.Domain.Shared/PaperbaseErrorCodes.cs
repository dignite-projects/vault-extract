namespace Dignite.Paperbase;

/// <summary>
/// 错误码字符串：是 i18n 字典匹配 key + 下游 consumer 按 code 分支处理的 wire-level 协议契约。
/// 必须是 <c>const</c>——任何运行时改动都会让 Localization/Paperbase/*.json 的映射失效，
/// 并破坏下游消费方既有的 try/catch (code == "Paperbase:Xxx") 分支逻辑。
/// 嵌套静态类仅按聚合分"抽屉"（文件柜式分组）——C# 标识符路径可调整，但**字符串值是冻结契约**，
/// 与 Localization/Paperbase/*.json 的 key 一一对应，不得改动。
/// </summary>
public static class PaperbaseErrorCodes
{
    public static class Document
    {
        public const string MarkdownIsImmutable = "Paperbase:MarkdownIsImmutable";
        public const string TitleIsImmutable = "Paperbase:TitleIsImmutable";
        public const string Duplicate = "Paperbase:DocumentDuplicate";
        public const string InRecycleBin = "Paperbase:DocumentInRecycleBin";
        public const string NotClassified = "Paperbase:DocumentNotClassified";
    }

    public static class DocumentType
    {
        public const string InvalidCode = "Paperbase:InvalidDocumentTypeCode";
        public const string InvalidCodeFormat = "Paperbase:InvalidDocumentTypeCodeFormat";
        public const string CodeAlreadyExists = "Paperbase:DocumentTypeCodeAlreadyExists";
        public const string InUse = "Paperbase:DocumentTypeInUse";
        public const string RestoreConflict = "Paperbase:DocumentTypeRestoreConflict";
        public const string InvalidDisplayName = "Paperbase:InvalidDocumentTypeDisplayName";
        public const string NoneConfigured = "Paperbase:NoDocumentTypesConfigured";
    }

    public static class FieldDefinition
    {
        public const string AlreadyExists = "Paperbase:FieldDefinitionAlreadyExists";
        public const string InvalidName = "Paperbase:InvalidFieldDefinitionName";
        public const string InvalidDisplayName = "Paperbase:InvalidFieldDefinitionDisplayName";
        public const string RestoreConflict = "Paperbase:FieldDefinitionRestoreConflict";
        public const string ParentTypeMissing = "Paperbase:FieldDefinitionParentTypeMissing";
        public const string DataTypeChangeNotAllowed = "Paperbase:FieldDefinitionDataTypeChangeNotAllowed";
    }

    public static class ExtractedField
    {
        public const string Unknown = "Paperbase:UnknownExtractedField";
        public const string InvalidValue = "Paperbase:InvalidExtractedFieldValue";
        public const string FieldTypeDoesNotSupportRange = "Paperbase:FieldTypeDoesNotSupportRange";
    }

    public static class Pipeline
    {
        public const string NotRetryable = "Paperbase:PipelineNotRetryable";
        public const string RetryInProgress = "Paperbase:PipelineRetryInProgress";
        public const string NeverRan = "Paperbase:PipelineNeverRan";
        public const string UnknownCode = "Paperbase:UnknownPipelineCode";
    }

    public static class Export
    {
        public const string InvalidTemplateName = "Paperbase:InvalidExportTemplateName";
        public const string TemplateNameAlreadyExists = "Paperbase:ExportTemplateNameAlreadyExists";
        public const string TemplateRequiresColumn = "Paperbase:ExportTemplateRequiresColumn";
        public const string TemplateTooManyColumns = "Paperbase:ExportTemplateTooManyColumns";
        public const string TemplateDuplicateColumnName = "Paperbase:ExportTemplateDuplicateColumnName";
        public const string InvalidColumnName = "Paperbase:InvalidExportColumnName";
        public const string DocumentLimitExceeded = "Paperbase:ExportDocumentLimitExceeded";
    }

    // 文件柜（#194）
    public static class Cabinet
    {
        public const string InvalidName = "Paperbase:InvalidCabinetName";
        public const string NameAlreadyExists = "Paperbase:CabinetNameAlreadyExists";
        public const string InvalidId = "Paperbase:InvalidCabinetId";
    }
}
