export const PAPERBASE_PERMISSIONS = {
  Documents: {
    Default: 'Paperbase.Documents',
    Upload: 'Paperbase.Documents.Upload',
    Delete: 'Paperbase.Documents.Delete',
    PermanentDelete: 'Paperbase.Documents.PermanentDelete',
    Restore: 'Paperbase.Documents.Restore',
    Export: 'Paperbase.Documents.Export',
    ConfirmClassification: 'Paperbase.Documents.ConfirmClassification',
    Pipelines: {
      Default: 'Paperbase.Documents.Pipelines',
      Retry: 'Paperbase.Documents.Pipelines.Retry',
    },
    // Batch reprocessing of existing documents (#289) — admin-level.
    Reprocessing: {
      Default: 'Paperbase.Documents.Reprocessing',
      FieldExtraction: 'Paperbase.Documents.Reprocessing.FieldExtraction',
      Reclassification: 'Paperbase.Documents.Reprocessing.Reclassification',
    },
    Templates: {
      Default: 'Paperbase.Documents.Templates',
      Create: 'Paperbase.Documents.Templates.Create',
      Update: 'Paperbase.Documents.Templates.Update',
      Delete: 'Paperbase.Documents.Templates.Delete',
    },
  },
  Cabinets: {
    Default: 'Paperbase.Cabinets',
    Create: 'Paperbase.Cabinets.Create',
    Update: 'Paperbase.Cabinets.Update',
    Delete: 'Paperbase.Cabinets.Delete',
  },
  // Document-type schema management (#217) — admin-level, independent of document CRUD.
  DocumentTypes: {
    Default: 'Paperbase.DocumentTypes',
    Create: 'Paperbase.DocumentTypes.Create',
    Update: 'Paperbase.DocumentTypes.Update',
    Delete: 'Paperbase.DocumentTypes.Delete',
  },
  // Field-definition schema management (#217) — admin-level, independent of document CRUD.
  FieldDefinitions: {
    Default: 'Paperbase.FieldDefinitions',
    Create: 'Paperbase.FieldDefinitions.Create',
    Update: 'Paperbase.FieldDefinitions.Update',
    Delete: 'Paperbase.FieldDefinitions.Delete',
  },
} as const;
