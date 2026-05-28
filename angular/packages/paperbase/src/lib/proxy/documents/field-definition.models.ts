import type { EntityDto } from '@abp/ng.core';
import type { FieldDataType } from './field-data-type.enum';

// Mirrors C# Dignite.Paperbase.Documents.Fields.FieldDefinitionDto.
// 类型绑定字段（B 机制）。按 (tenantId, documentTypeCode, name) 唯一；
// tenantId == null 为 Host 字段，否则为该租户字段（两层互斥，不混合）。
export interface FieldDefinitionDto extends EntityDto<string> {
  tenantId?: string;
  documentTypeCode: string;
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
}

export interface CreateFieldDefinitionDto {
  documentTypeCode: string;
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
}

export interface UpdateFieldDefinitionDto {
  name: string;
  displayName: string;
  prompt: string;
  dataType: FieldDataType;
  displayOrder: number;
  isRequired: boolean;
}
