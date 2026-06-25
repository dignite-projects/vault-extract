import type { FieldDataType } from './field-data-type.enum';
import type { EntityDto } from '@abp/ng.core';

export interface CreateFieldDefinitionDto {
  documentTypeId: string;
  name: string;
  displayName: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
  isUniqueKey?: boolean;
}

export interface DraftFieldDefinitionInput {
  prompt: string;
  forNewField?: boolean;
}

export interface FieldDefinitionDraftDto {
  displayName?: string;
  name?: string;
  dataType?: FieldDataType;
  isRequired?: boolean;
  allowMultiple?: boolean;
}

export interface FieldDefinitionDto extends EntityDto<string> {
  tenantId?: string | null;
  documentTypeId?: string;
  name?: string;
  displayName?: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
  isUniqueKey?: boolean;
}

export interface GetFieldDefinitionListInput {
  documentTypeId?: string | null;
  onlyDeleted?: boolean;
}

export interface UpdateFieldDefinitionDto {
  name: string;
  displayName: string;
  prompt?: string | null;
  dataType?: FieldDataType;
  displayOrder?: number;
  isRequired?: boolean;
  allowMultiple?: boolean;
  isUniqueKey?: boolean;
}
