import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { FieldReextractionPreviewDto, ReclassificationPreviewDto, ReclassificationScopeInput, ReprocessingStartResultDto, StartFieldReextractionInput } from '../../../documents/reprocessing/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentReprocessingService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  previewFieldExtraction = (documentTypeId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldReextractionPreviewDto>({
      method: 'GET',
      url: '/api/vault-extract/document-reprocessing/field-extraction/preview',
      params: { documentTypeId },
    },
    { apiName: this.apiName,...config });
  

  previewReclassification = (input: ReclassificationScopeInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReclassificationPreviewDto>({
      method: 'POST',
      url: '/api/vault-extract/document-reprocessing/reclassification/preview',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  startFieldExtraction = (input: StartFieldReextractionInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReprocessingStartResultDto>({
      method: 'POST',
      url: '/api/vault-extract/document-reprocessing/field-extraction',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  startReclassification = (input: ReclassificationScopeInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReprocessingStartResultDto>({
      method: 'POST',
      url: '/api/vault-extract/document-reprocessing/reclassification',
      body: input,
    },
    { apiName: this.apiName,...config });
}