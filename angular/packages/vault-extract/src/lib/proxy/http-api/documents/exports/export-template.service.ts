import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { CreateExportTemplateDto, ExportDocumentsInput, ExportTemplateDto, UpdateExportTemplateDto } from '../../../documents/exports/models';

@Injectable({
  providedIn: 'root',
})
export class ExportTemplateService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  create = (input: CreateExportTemplateDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ExportTemplateDto>({
      method: 'POST',
      url: '/api/vault-extract/export-templates',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  delete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/vault-extract/export-templates/${id}`,
    },
    { apiName: this.apiName,...config });
  

  export = (input: ExportDocumentsInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, Blob>({
      method: 'POST',
      responseType: 'blob',
      url: '/api/vault-extract/export-templates/export',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  get = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ExportTemplateDto>({
      method: 'GET',
      url: `/api/vault-extract/export-templates/${id}`,
    },
    { apiName: this.apiName,...config });
  

  getList = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, ExportTemplateDto[]>({
      method: 'GET',
      url: '/api/vault-extract/export-templates',
    },
    { apiName: this.apiName,...config });
  

  update = (id: string, input: UpdateExportTemplateDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ExportTemplateDto>({
      method: 'PUT',
      url: `/api/vault-extract/export-templates/${id}`,
      body: input,
    },
    { apiName: this.apiName,...config });
}