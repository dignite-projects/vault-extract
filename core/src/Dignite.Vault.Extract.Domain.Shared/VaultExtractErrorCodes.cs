namespace Dignite.Vault.Extract;

/// <summary>
/// Error-code strings: wire-level protocol contract used as i18n dictionary keys and by downstream
/// consumers branching on code. They must be <c>const</c>: any runtime change would break mappings in
/// Localization/Extract/*.json and downstream try/catch logic such as
/// (code == "Extract:Xxx"). Nested static classes are only aggregate-based grouping drawers; C#
/// identifier paths can be adjusted, but <b>string values are frozen contracts</b> and correspond
/// one-to-one with Localization/Extract/*.json keys.
/// </summary>
public static class VaultExtractErrorCodes
{
    public static class Document
    {
        public const string MarkdownIsImmutable = "Extract:MarkdownIsImmutable";
        public const string TitleIsImmutable = "Extract:TitleIsImmutable";
        public const string Duplicate = "Extract:DocumentDuplicate";
        public const string InRecycleBin = "Extract:DocumentInRecycleBin";
        public const string NotClassified = "Extract:DocumentNotClassified";
        // #263: prerequisite for "re-recognize" (rerun automatic classification). Automatic
        // classification input is Document.Markdown, so without text extraction output there is
        // nothing to reclassify.
        public const string NotTextExtracted = "Extract:DocumentNotTextExtracted";
        // #221: upload fail-closed validation failure codes (size exceeded / content-type + extension
        // not in whitelist).
        public const string FileTooLarge = "Extract:DocumentFileTooLarge";
        public const string UnsupportedFileType = "Extract:DocumentUnsupportedFileType";
        // #477: a figures/{hash} egress request whose hash is not in the document's retained-figure manifest
        // (retention was off when it was processed, or a stale / invalid reference).
        public const string FigureNotFound = "Extract:DocumentFigureNotFound";
    }

    public static class DocumentType
    {
        public const string InvalidCodeFormat = "Extract:InvalidDocumentTypeCodeFormat";
        public const string CodeAlreadyExists = "Extract:DocumentTypeCodeAlreadyExists";
        public const string InUse = "Extract:DocumentTypeInUse";
        public const string RestoreConflict = "Extract:DocumentTypeRestoreConflict";
        public const string InvalidDisplayName = "Extract:InvalidDocumentTypeDisplayName";
        public const string InvalidDescription = "Extract:InvalidDocumentTypeDescription";
        public const string NoneConfigured = "Extract:NoDocumentTypesConfigured";
    }

    // #444: config import/export ("pack") mechanism for a document type + its field definitions.
    public static class DocumentTypePack
    {
        public const string UnsupportedVersion = "Extract:DocumentTypePackUnsupportedVersion";
    }

    public static class FieldDefinition
    {
        public const string AlreadyExists = "Extract:FieldDefinitionAlreadyExists";
        public const string InvalidName = "Extract:InvalidFieldDefinitionName";
        public const string InvalidDisplayName = "Extract:InvalidFieldDefinitionDisplayName";
        public const string RestoreConflict = "Extract:FieldDefinitionRestoreConflict";
        public const string ParentTypeMissing = "Extract:FieldDefinitionParentTypeMissing";
        public const string DataTypeChangeNotAllowed = "Extract:FieldDefinitionDataTypeChangeNotAllowed";
        public const string MultiValueRequiresStringType = "Extract:FieldDefinitionMultiValueRequiresStringType";
        public const string MultiValueChangeNotAllowed = "Extract:FieldDefinitionMultiValueChangeNotAllowed";
    }

    public static class ExtractedField
    {
        public const string Unknown = "Extract:UnknownExtractedField";
        public const string InvalidValue = "Extract:InvalidExtractedFieldValue";
        public const string FieldTypeDoesNotSupportRange = "Extract:FieldTypeDoesNotSupportRange";
        public const string FieldTypeNotQueryable = "Extract:FieldTypeNotQueryable";
    }

    public static class Pipeline
    {
        public const string NotRetryable = "Extract:PipelineNotRetryable";
        public const string RetryInProgress = "Extract:PipelineRetryInProgress";
        public const string NeverRan = "Extract:PipelineNeverRan";
        public const string UnknownCode = "Extract:UnknownPipelineCode";
    }

    public static class Export
    {
        public const string InvalidTemplateName = "Extract:InvalidExportTemplateName";
        public const string TemplateNameAlreadyExists = "Extract:ExportTemplateNameAlreadyExists";
        public const string TemplateRequiresColumn = "Extract:ExportTemplateRequiresColumn";
        public const string TemplateTooManyColumns = "Extract:ExportTemplateTooManyColumns";
        public const string TemplateDuplicateField = "Extract:ExportTemplateDuplicateField";
        public const string DocumentLimitExceeded = "Extract:ExportDocumentLimitExceeded";
    }

    // Cabinets (#194).
    public static class Cabinet
    {
        public const string InvalidName = "Extract:InvalidCabinetName";
        public const string InvalidDescription = "Extract:InvalidCabinetDescription";
        public const string NameAlreadyExists = "Extract:CabinetNameAlreadyExists";
        public const string InvalidId = "Extract:InvalidCabinetId";
    }
}
