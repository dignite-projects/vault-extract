import type { EntityDto } from '@abp/ng.core';

// Mirrors C# Dignite.Paperbase.Documents.DocumentTypes.DocumentTypeDto.
// 文档类型（字段架构 v2）。GetVisible 返回当前层（Host 或当前租户）；
// Create/Update/Delete/Restore 只作用于当前租户私有类型（tenantId == CurrentTenant.Id）。
export interface DocumentTypeDto extends EntityDto<string> {
  tenantId?: string;
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}

export interface CreateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}

export interface UpdateDocumentTypeDto {
  typeCode: string;
  displayName: string;
  confidenceThreshold: number;
  priority: number;
}
