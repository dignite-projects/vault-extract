import type { EntityDto } from '@abp/ng.core';

export interface CreateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
}

export interface DocumentTypeDto extends EntityDto<string> {
  tenantId?: string | null;
  typeCode?: string;
  displayName?: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
}

export interface UpdateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  description?: string | null;
  confidenceThreshold?: number;
  priority?: number;
}
