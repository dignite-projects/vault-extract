import type { ExportFormat } from './export-format.enum';
import type { DocumentLifecycleStatus } from '../document-lifecycle-status.enum';
import type { EntityDto } from '@abp/ng.core';

export interface CreateExportTemplateDto {
  name: string;
  format?: ExportFormat;
  documentTypeId: string;
  columns: ExportColumnInput[];
}

export interface ExportColumnDto {
  fieldDefinitionId?: string;
  order?: number;
}

export interface ExportColumnInput {
  fieldDefinitionId: string;
  order?: number;
}

export interface ExportDocumentsInput {
  templateId?: string;
  documentIds?: string[] | null;
  lifecycleStatus?: DocumentLifecycleStatus | null;
  cabinetId?: string | null;
  creationTimeMin?: string | null;
  creationTimeMax?: string | null;
}

export interface ExportTemplateDto extends EntityDto<string> {
  tenantId?: string | null;
  name?: string;
  format?: ExportFormat;
  documentTypeId?: string;
  columns?: ExportColumnDto[];
}

export interface UpdateExportTemplateDto {
  name: string;
  format?: ExportFormat;
  documentTypeId: string;
  columns: ExportColumnInput[];
}
