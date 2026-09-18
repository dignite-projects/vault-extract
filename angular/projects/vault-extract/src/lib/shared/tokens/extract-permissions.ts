export const EXTRACT_PERMISSIONS = {
  Documents: {
    // #632 decision 1: ENTRY, not "read everything" — "may enter the documents area and work inside
    // the caller's own type scope". It is still the route gate and the parent every child below
    // implies; the module-wide "read every type of the layer" half is ReadAll.
    Default: 'VaultExtract.Documents',
    // #632: the module-wide read. Without it a caller sees only the types it holds a
    // Resources.Read grant on, and never sees untyped documents at all. Also gates the
    // whole-layer statistics endpoint (DocumentStatisticsAppService), hence the overview card.
    ReadAll: 'VaultExtract.Documents.ReadAll',
    // #645: upload into EVERY type, including an untyped upload the AI classifies. A type-level Upload
    // grant alone (Resources.Upload) admits an upload into that one type; this is not also required.
    Upload: 'VaultExtract.Documents.Upload',
    // #645: delete AND restore, for every type — whoever may delete may undo. There is no separate restore
    // permission any more; a type-level Delete grant covers both for its type, and an owner both for their own.
    Delete: 'VaultExtract.Documents.Delete',
    PermanentDelete: 'VaultExtract.Documents.PermanentDelete',
    Export: 'VaultExtract.Documents.Export',
    ConfirmClassification: 'VaultExtract.Documents.ConfirmClassification',
    Pipelines: {
      Default: 'VaultExtract.Documents.Pipelines',
      Retry: 'VaultExtract.Documents.Pipelines.Retry',
    },
    // Batch reprocessing of existing documents (#289) — admin-level.
    Reprocessing: {
      Default: 'VaultExtract.Documents.Reprocessing',
      FieldExtraction: 'VaultExtract.Documents.Reprocessing.FieldExtraction',
      Reclassification: 'VaultExtract.Documents.Reprocessing.Reclassification',
    },
  },
  Cabinets: {
    Default: 'VaultExtract.Cabinets',
    Create: 'VaultExtract.Cabinets.Create',
    Update: 'VaultExtract.Cabinets.Update',
    Delete: 'VaultExtract.Cabinets.Delete',
  },
  // Document-type schema management (#217) — admin-level, independent of document CRUD.
  DocumentTypes: {
    Default: 'VaultExtract.DocumentTypes',
    Create: 'VaultExtract.DocumentTypes.Create',
    Update: 'VaultExtract.DocumentTypes.Update',
    Delete: 'VaultExtract.DocumentTypes.Delete',
    // May open the per-type resource-permission dialog and grant/revoke the four Resources
    // grants below (#629, completed by #632). ABP renders one checkbox per registered
    // definition, so the dialog itself needed no work.
    ManagePermissions: 'VaultExtract.DocumentTypes.ManagePermissions',
    // ABP resource-permission strings (#629/#632) — frozen contracts mirrored from
    // VaultExtractResourcePermissions (#636 renamed the C# holder out of VaultExtractPermissions.DocumentTypes;
    // the string values did not change), NOT standard permissions: they are only ever checked against one
    // DocumentType row (resourceKey = its Id), never by name alone. The client never checks them against
    // PermissionService either: they arrive per type on DocumentTypeDto.resourcePermissions from GetVisibleAsync,
    // and document-rights.ts pairs each with its module-wide counterpart.
    Resources: {
      Name: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType',
      // Declare / assign this type — at upload, and as the TARGET of confirm / reclassify.
      Upload: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Upload',
      // Read the documents of this type (detail, blob, list / export rows, pipeline runs).
      Read: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Read',
      // Run the operator edit family on the documents of this type (confirm / reclassify /
      // re-recognize / re-extract / update fields / correct Markdown / reject / allow duplicate /
      // resolve field-validation warnings).
      Edit: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Edit',
      // Soft-delete the documents of this type (#632). Restore reuses this same grant — whoever may delete may
      // undo — while permanent delete stays module-wide only.
      Delete: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Delete',
    },
  },
  // Field-definition schema management (#217) — admin-level, independent of document CRUD.
  FieldDefinitions: {
    Default: 'VaultExtract.FieldDefinitions',
    Create: 'VaultExtract.FieldDefinitions.Create',
    Update: 'VaultExtract.FieldDefinitions.Update',
    Delete: 'VaultExtract.FieldDefinitions.Delete',
  },
} as const;
