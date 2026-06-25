import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DocumentPipelineRunDto } from '../../../documents/pipelines/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentPipelineRunService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  getList = (documentId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentPipelineRunDto[]>({
      method: 'GET',
      url: '/api/extract/document-pipeline-runs',
      params: { documentId },
    },
    { apiName: this.apiName,...config });
}