import type { EntityDto, PagedAndSortedResultRequestDto } from '@abp/ng.core';
import type { DocumentLifecycleStatus } from './document-lifecycle-status.enum';
import type { DocumentReviewStatus } from './document-review-status.enum';
import type { IRemoteStreamContent } from '../volo/abp/content/models';

export interface ConfirmClassificationInput {
  documentTypeId: string;
}

export interface DocumentDto extends EntityDto<string> {
  tenantId?: string | null;
  fileOrigin?: FileOriginDto;
  cabinetId?: string | null;
  documentTypeCode?: string | null;
  lifecycleStatus?: DocumentLifecycleStatus;
  reviewStatus?: DocumentReviewStatus;
  classificationConfidence?: number;
  classificationReason?: string | null;
  title?: string | null;
  markdown?: string | null;
  language?: string | null;
  extractedFields?: Record<string, any> | null;
  creationTime?: string;
}

export interface DocumentFieldFilter {
  name: string | null;
  value?: string | null;
  min?: string | null;
  max?: string | null;
}

export interface DocumentListItemDto extends EntityDto<string> {
  tenantId?: string | null;
  fileOrigin?: FileOriginDto;
  cabinetId?: string | null;
  documentTypeCode?: string | null;
  lifecycleStatus?: DocumentLifecycleStatus;
  reviewStatus?: DocumentReviewStatus;
  classificationConfidence?: number;
  title?: string | null;
  creationTime?: string;
  deletionTime?: string | null;
  extractedFields?: Record<string, any> | null;
}

export interface FileOriginDto {
  uploadedByUserName?: string;
  originalFileName?: string | null;
  contentType?: string;
  fileSize?: number;
}

export interface GetDocumentListInput extends PagedAndSortedResultRequestDto {
  lifecycleStatus?: DocumentLifecycleStatus | null;
  documentTypeCode?: string | null;
  reviewStatus?: DocumentReviewStatus | null;
  isDeleted?: boolean | null;
  cabinetId?: string | null;
  fieldFilters?: DocumentFieldFilter[] | null;
}

export interface ReclassifyDocumentInput {
  documentTypeId: string;
}

export interface RejectReviewInput {
  reason?: string | null;
}

export interface RetryPipelineInput {
  pipelineCode: string;
}

export interface UpdateDocumentCabinetInput {
  cabinetId?: string | null;
}

export interface UpdateExtractedFieldsInput {
  fields?: Record<string, any>;
}

export interface UploadDocumentInput {
  file: IRemoteStreamContent;
  cabinetId?: string | null;
}
