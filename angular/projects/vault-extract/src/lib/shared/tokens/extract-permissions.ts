export const EXTRACT_PERMISSIONS = {
  Documents: {
    Default: 'VaultExtract.Documents',
    Upload: 'VaultExtract.Documents.Upload',
    Delete: 'VaultExtract.Documents.Delete',
    PermanentDelete: 'VaultExtract.Documents.PermanentDelete',
    Restore: 'VaultExtract.Documents.Restore',
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
    // May open the per-type resource-permission dialog and grant/revoke the Resources.Upload
    // grant below (#629).
    ManagePermissions: 'VaultExtract.DocumentTypes.ManagePermissions',
    // ABP resource-permission strings (#629) — frozen contracts mirrored from
    // VaultExtractPermissions.DocumentTypes.Resources, NOT standard permissions: they are only
    // ever checked against one DocumentType row (resourceKey = its Id), never by name alone.
    Resources: {
      Name: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType',
      Upload: 'Dignite.Vault.Extract.Documents.DocumentTypes.DocumentType.Upload',
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
