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
        // #555: an operator Markdown correction (UpdateMarkdownAsync) is forbidden on a container — it runs
        // no field extraction and its Markdown is only a provenance anchor, so a correction has no sensible
        // meaning here. Deferred to a future issue if ever needed.
        public const string CannotCorrectContainerMarkdown = "Extract:CannotCorrectContainerMarkdown";
        public const string Duplicate = "Extract:DocumentDuplicate";
        public const string InRecycleBin = "Extract:DocumentInRecycleBin";
        public const string NotClassified = "Extract:DocumentNotClassified";
        // #263: prerequisite for the operations that run on existing Markdown (#660: re-parse replaces it, so it
        // must exist; a failed first parse is RetryPipelineAsync's job). Without text extraction output there is
        // nothing to reclassify, re-extract or replace.
        public const string NotTextExtracted = "Extract:DocumentNotTextExtracted";
        // #660: re-parse refused on a sub-document. It has no file of its own (#487) — its text is a slice of the
        // parent's Markdown — so the only way to re-parse it is to re-parse the parent, which re-splits it.
        public const string ReparseSubDocument = "Extract:DocumentReparseSubDocument";
        // #660: re-parse refused because the original file is not in blob storage (no FileOrigin, or the blob is
        // gone). Refused up front: accepted, the background run would fail and leave a document whose text is
        // intact marked Failed, and every retry would fail the same way. Distinct from NoSourceBlob, whose message
        // is about downloading.
        public const string ReparseSourceFileMissing = "Extract:DocumentReparseSourceFileMissing";
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
        // #660: restoring a sub-document whose ledger row is gone. Its parent's segmentation no longer routes it — a
        // re-parse withdrew it and re-split the new Markdown, or a container→concrete reclassify retracted it — so
        // restoring it would revive a child next to the ones that replaced it ("live routed child ⟺ its ledger row
        // exists", .claude/rules/sub-document-segmentation.md). Remedy: re-parse the parent.
        public const string RestoreSuperseded = "Extract:DocumentRestoreSuperseded";
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
        public const string SchemaPromptBudgetExceeded = "Extract:FieldDefinitionSchemaPromptBudgetExceeded";

        /// <summary>
        /// The submitted <c>FieldTypeName</c> matches no registered field type (#559). A registration key
        /// rather than a closed enum is what buys v3 its extensibility, and this is the cost: the value is
        /// only checkable at runtime, so it is checked at the write boundary.
        /// </summary>
        public const string UnknownFieldType = "Extract:UnknownFieldType";

        /// <summary>
        /// A composite field (<c>Table</c>, #625) declares one of its own columns under a
        /// <c>FieldTypeName</c> this deployment has never wired Vault-Extract-side support for. Checked
        /// recursively alongside <see cref="UnknownFieldType"/> at the same create/update boundary, so a
        /// bad column type is rejected before it is ever saved rather than discovered only when field
        /// extraction tries to build its schema.
        /// </summary>
        public const string UnknownColumnFieldType = "Extract:UnknownColumnFieldType";

        /// <summary>
        /// A composite field's (<c>Table</c>) column <c>Name</c> fails the same allow-list
        /// <c>Field.SetName</c> enforces on a top-level field's name. A column name is concatenated raw
        /// into the LLM's JSON schema message (<c>TableFieldTypeExtension.BuildExtractionSchema</c> uses
        /// it verbatim as a property key), so it is the same prompt-injection boundary a top-level
        /// <c>Name</c> already has — not a formatting preference. Checked recursively alongside
        /// <see cref="UnknownColumnFieldType"/> at the same create/update boundary.
        /// </summary>
        public const string InvalidColumnName = "Extract:InvalidColumnName";

        /// <summary>
        /// A field definition nests composite field types (<c>Table</c>) deeper than the kernel's own
        /// <c>CompositeFieldNesting.MaxDepth</c> allows. Checked first, before anything here recurses into
        /// an unvetted configuration — the kernel measures the depth, the host (this check) enforces it.
        /// </summary>
        public const string CompositeNestingTooDeep = "Extract:CompositeNestingTooDeep";

        /// <summary>
        /// A field marked <c>IsSearchable</c> whose field type has no query-index slot (#562) — e.g.
        /// long text. The value would never reach the index, so the flag would silently do nothing.
        /// </summary>
        public const string FieldTypeNotSearchable = "Extract:FieldTypeNotSearchable";

        /// <summary>
        /// A field marked <c>IsUniqueKey</c> whose field type has no query-index slot (#626) — e.g. Table or
        /// CKEditor. Duplicate-detection fingerprinting reads straight from the value bag, not the index, so this
        /// isn't about the fingerprint mechanism itself; it's a deliberate product restriction: a composite/long-text
        /// value is not something you'd sensibly identify a document by, and tying eligibility to the same
        /// indexability predicate CheckSearchable already uses means any future non-indexable type (e.g. a Matrix
        /// composite type, not yet implemented) is excluded from IsUniqueKey by construction.
        /// </summary>
        public const string FieldTypeNotUniqueKeyable = "Extract:FieldTypeNotUniqueKeyable";
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
