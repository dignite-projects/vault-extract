export const EXTRACT_PERMISSIONS = {
  Documents: {
    Default: 'Extract.Documents',
    Upload: 'Extract.Documents.Upload',
    Delete: 'Extract.Documents.Delete',
    PermanentDelete: 'Extract.Documents.PermanentDelete',
    Restore: 'Extract.Documents.Restore',
    Export: 'Extract.Documents.Export',
    ConfirmClassification: 'Extract.Documents.ConfirmClassification',
    Pipelines: {
      Default: 'Extract.Documents.Pipelines',
      Retry: 'Extract.Documents.Pipelines.Retry',
    },
    // Batch reprocessing of existing documents (#289) — admin-level.
    Reprocessing: {
      Default: 'Extract.Documents.Reprocessing',
      FieldExtraction: 'Extract.Documents.Reprocessing.FieldExtraction',
      Reclassification: 'Extract.Documents.Reprocessing.Reclassification',
    },
    Templates: {
      Default: 'Extract.Documents.Templates',
      Create: 'Extract.Documents.Templates.Create',
      Update: 'Extract.Documents.Templates.Update',
      Delete: 'Extract.Documents.Templates.Delete',
    },
  },
  Cabinets: {
    Default: 'Extract.Cabinets',
    Create: 'Extract.Cabinets.Create',
    Update: 'Extract.Cabinets.Update',
    Delete: 'Extract.Cabinets.Delete',
  },
  // Document-type schema management (#217) — admin-level, independent of document CRUD.
  DocumentTypes: {
    Default: 'Extract.DocumentTypes',
    Create: 'Extract.DocumentTypes.Create',
    Update: 'Extract.DocumentTypes.Update',
    Delete: 'Extract.DocumentTypes.Delete',
  },
  // Field-definition schema management (#217) — admin-level, independent of document CRUD.
  FieldDefinitions: {
    Default: 'Extract.FieldDefinitions',
    Create: 'Extract.FieldDefinitions.Create',
    Update: 'Extract.FieldDefinitions.Update',
    Delete: 'Extract.FieldDefinitions.Delete',
  },
} as const;
