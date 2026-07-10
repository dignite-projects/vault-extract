import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DocumentTypePackDto, DocumentTypePackImportResultDto, ImportDocumentTypePacksInput } from '../../../../documents/document-types/packs/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentTypePackService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  export = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypePackDto>({
      method: 'GET',
      url: `/api/vault-extract/document-type-packs/${id}`,
    },
    { apiName: this.apiName,...config });
  

  exportAll = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypePackDto[]>({
      method: 'GET',
      url: '/api/vault-extract/document-type-packs',
    },
    { apiName: this.apiName,...config });
  

  import = (input: ImportDocumentTypePacksInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypePackImportResultDto>({
      method: 'POST',
      url: '/api/vault-extract/document-type-packs/import',
      body: input,
    },
    { apiName: this.apiName,...config });
}