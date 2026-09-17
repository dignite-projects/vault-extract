import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { CreateDocumentTypeDto, DocumentTypeDto, UpdateDocumentTypeDto } from '../../../documents/document-types/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentTypeService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  create = (input: CreateDocumentTypeDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypeDto>({
      method: 'POST',
      url: '/api/vault-extract/document-types',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  delete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/vault-extract/document-types/${id}`,
    },
    { apiName: this.apiName,...config });
  

  getDeleted = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypeDto[]>({
      method: 'GET',
      url: '/api/vault-extract/document-types/deleted',
    },
    { apiName: this.apiName,...config });
  

  getVisible = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypeDto[]>({
      method: 'GET',
      url: '/api/vault-extract/document-types',
    },
    { apiName: this.apiName,...config });
  

  restore = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypeDto>({
      method: 'POST',
      url: `/api/vault-extract/document-types/${id}/restore`,
    },
    { apiName: this.apiName,...config });
  

  update = (id: string, input: UpdateDocumentTypeDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentTypeDto>({
      method: 'PUT',
      url: `/api/vault-extract/document-types/${id}`,
      body: input,
    },
    { apiName: this.apiName,...config });
}