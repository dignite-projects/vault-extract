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
        // #485 (B1): re-added after #481 removed it assuming FileOrigin can never be null. Same string as before
        // #481 (nothing consumed it in the interim, so this is not a wire break) — a legacy pre-#481 derived row
        // can still carry a null FileOrigin during the documented binaries-first deploy window.
        public const string NoSourceBlob = "Extract:DocumentNoSourceBlob";
        // #485 (A3): restoring a derived document is rejected when another LIVE document already occupies the
        // same (OriginDocumentId, OriginConstituentKey) identity — the application-layer fail-close that replaces
        // the fail-close the #481-dropped #391 filtered-unique index used to give for free at restore time.
        public const string RestoreConflict = "Extract:DocumentRestoreConflict";
        // #508: a source still has LIVE derived sub-documents, so soft-deleting it is blocked — they would be left
        // with a dangling OriginDocumentId, which since #487 is their ONLY route to a source file (they carry no
        // FileOrigin of their own). Same string as before #481 removed it; nothing consumed it in the interim,
        // so re-adding it is not a wire break.
        public const string HasSubDocuments = "Extract:DocumentHasSubDocuments";
        // #508: the permanent-delete twin of the above, and strictly stronger — children already in the recycle bin
        // count too, because hard-deleting the source reclaims the blob they reach through OriginDocumentId and a
        // recycle-bin child is restorable. A distinct code because the remedy differs: the operator must look in
        // the recycle bin, not just the document list.
        public const string HasSubDocumentsPermanentDelete = "Extract:DocumentHasSubDocumentsPermanentDelete";
        // #531: the document carries a DocumentTypeId whose type is no longer active in its layer, so restoring it
        // would revive a live document referencing a deleted type — the UI can then only fall back to the raw type
        // code, its fields cannot be edited, and field re-extraction silently skips it. DocumentType is schema
        // identity, not optional metadata, so restore fails closed rather than reviving a partially usable document.
        // DocumentTypeAppService.DeleteAsync's all-document guard makes this unreachable going forward; this is the
        // defense-in-depth for legacy rows, manual DB edits, and a delete/classification race. Remedy: restore the
        // type first, then the document.
        public const string RestoreTypeDeleted = "Extract:DocumentRestoreTypeDeleted";
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
        // The five Extract:ExportTemplate* codes died with the template layer (#499). The wire value below is
        // frozen and unchanged.
        public const string DocumentLimitExceeded = "Extract:ExportDocumentLimitExceeded";

        // #501 item 2: the column bound, restored after #499 deleted ExportTemplateConsts.MaxColumnCount along
        // with the template that enforced it. Keeps the "Extract:Export*" shape of its row-bound twin above.
        public const string ColumnLimitExceeded = "Extract:ExportColumnLimitExceeded";
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
