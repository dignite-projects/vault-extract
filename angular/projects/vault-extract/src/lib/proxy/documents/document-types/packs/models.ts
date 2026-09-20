import type { DuplicateDetectionScope } from '../duplicate-detection-scope.enum';
import type { FieldDataType } from '../../fields/field-data-type.enum';
import type { PackItemAction } from './pack-item-action.enum';
import type { PackImportMode } from './pack-import-mode.enum';

export interface DocumentTypePackDto {
  version?: number;
  typeCode: string;
  displayName: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
  duplicateScope?: DuplicateDetectionScope;
  fields?: DocumentTypePackFieldDto[];
}

export interface DocumentTypePackFieldDto {
  name: string;
  displayName: string;
  description?: string | null;
  fieldTypeName?: string | null;
  configuration?: Record<string, object> | null;
  displayOrder?: number;
  isRequired?: boolean;
  isSearchable?: boolean;
  isUniqueKey?: boolean;
  prompt?: string | null;
  dataType?: FieldDataType | null;
  allowMultiple?: boolean;
}

export interface DocumentTypePackImportResultDto {
  items?: DocumentTypePackItemResultDto[];
  typesCreated?: number;
  typesUpdated?: number;
  typesSkipped?: number;
  fieldsCreated?: number;
  fieldsUpdated?: number;
  fieldsSkipped?: number;
}

export interface DocumentTypePackItemResultDto {
  typeCode?: string;
  typeAction?: PackItemAction;
  fieldsCreated?: number;
  fieldsUpdated?: number;
  fieldsSkipped?: number;
}

export interface ImportDocumentTypePacksInput {
  packs: DocumentTypePackDto[];
  mode?: PackImportMode;
}
