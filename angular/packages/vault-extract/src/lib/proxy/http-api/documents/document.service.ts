import { RestService, Rest } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { ConfirmClassificationInput, DocumentDto, DocumentListItemDto, GetDocumentListInput, ReclassifyDocumentInput, RejectReviewInput, ResolveFieldValidationWarningsInput, RetryPipelineInput, UpdateDocumentCabinetInput, UpdateExtractedFieldsInput, UploadDocumentInput } from '../../documents/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  allowDuplicate = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/review/allow-duplicate`,
    },
    { apiName: this.apiName,...config });
  

  confirmClassification = (id: string, input: ConfirmClassificationInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/confirm-classification`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  delete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/vault-extract/documents/${id}`,
    },
    { apiName: this.apiName,...config });
  

  get = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'GET',
      url: `/api/vault-extract/documents/${id}`,
    },
    { apiName: this.apiName,...config });
  

  getBlob = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, Blob>({
      method: 'GET',
      responseType: 'blob',
      url: `/api/vault-extract/documents/${id}/blob`,
    },
    { apiName: this.apiName,...config });
  

  getList = (input: GetDocumentListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, PagedResultDto<DocumentListItemDto>>({
      method: 'GET',
      url: '/api/vault-extract/documents',
      params: { lifecycleStatus: input.lifecycleStatus, documentTypeCode: input.documentTypeCode, reviewDisposition: input.reviewDisposition, hasReviewReasons: input.hasReviewReasons, isDeleted: input.isDeleted, cabinetId: input.cabinetId, originDocumentId: input.originDocumentId, fieldFilters: input.fieldFilters, sorting: input.sorting, skipCount: input.skipCount, maxResultCount: input.maxResultCount },
    },
    { apiName: this.apiName,...config });
  

  permanentDelete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/vault-extract/documents/${id}/permanent`,
    },
    { apiName: this.apiName,...config });
  

  reclassify = (id: string, input: ReclassifyDocumentInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/reclassify`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  reextractFields = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/reextract-fields`,
    },
    { apiName: this.apiName,...config });
  

  rejectReview = (id: string, input: RejectReviewInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/review/reject`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  rerecognize = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/rerecognize`,
    },
    { apiName: this.apiName,...config });
  

  resolveFieldValidationWarnings = (id: string, input: ResolveFieldValidationWarningsInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/review/resolve-field-validation-warnings`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  restore = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/restore`,
    },
    { apiName: this.apiName,...config });
  

  retryPipeline = (id: string, input: RetryPipelineInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/retry-pipeline`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  updateCabinet = (id: string, input: UpdateDocumentCabinetInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/cabinet`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  updateExtractedFields = (id: string, input: UpdateExtractedFieldsInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: `/api/vault-extract/documents/${id}/extracted-fields`,
      body: input,
    },
    { apiName: this.apiName,...config });
  

  upload = (input: UploadDocumentInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentDto>({
      method: 'POST',
      url: '/api/vault-extract/documents/upload',
      params: { cabinetId: input.cabinetId },
      body: input.file,
    },
    { apiName: this.apiName,...config });
}