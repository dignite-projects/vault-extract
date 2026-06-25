import type { ReclassificationScope } from './reclassification-scope.enum';

export interface FieldReextractionPreviewDto {
  documentTypeId?: string;
  documentCount?: number;
  fieldNames?: string[];
}

export interface ReclassificationPreviewDto {
  documentCount?: number;
}

export interface ReclassificationScopeInput {
  scope: ReclassificationScope;
  documentTypeId?: string | null;
  includeManuallyConfirmed?: boolean;
}

export interface ReprocessingStartResultDto {
  estimatedDocumentCount?: number;
}

export interface StartFieldReextractionInput {
  documentTypeId: string;
}
