import type { DuplicateDetectionScope } from './duplicate-detection-scope.enum';
import type { EntityDto } from '@abp/ng.core';

export interface CreateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
  duplicateScope?: DuplicateDetectionScope;
}

export interface DocumentTypeDto extends EntityDto<string> {
  tenantId?: string | null;
  typeCode?: string;
  displayName?: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
  duplicateScope?: DuplicateDetectionScope;
  resourcePermissions?: Record<string, boolean>;
}

export interface DuplicateScopePreviewDto {
  currentScope?: DuplicateDetectionScope;
  prospectiveScope?: DuplicateDetectionScope;
  wouldChange?: boolean;
  willFlagCount?: number;
  willClearCount?: number;
}

export interface UpdateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
  duplicateScope?: DuplicateDetectionScope;
}
