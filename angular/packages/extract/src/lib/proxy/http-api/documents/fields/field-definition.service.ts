import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { CreateFieldDefinitionDto, FieldDefinitionDto, GetFieldDefinitionListInput, UpdateFieldDefinitionDto } from '../../../documents/fields/models';

@Injectable({
  providedIn: 'root',
})
export class FieldDefinitionService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  create = (input: CreateFieldDefinitionDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldDefinitionDto>({
      method: 'POST',
      url: '/api/extract/field-definitions',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  delete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/extract/field-definitions/${id}`,
    },
    { apiName: this.apiName,...config });
  

  getList = (input: GetFieldDefinitionListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldDefinitionDto[]>({
      method: 'GET',
      url: '/api/extract/field-definitions',
      params: { documentTypeId: input.documentTypeId, onlyDeleted: input.onlyDeleted },
    },
    { apiName: this.apiName,...config });
  

  restore = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldDefinitionDto>({
      method: 'POST',
      url: `/api/extract/field-definitions/${id}/restore`,
    },
    { apiName: this.apiName,...config });
  

  update = (id: string, input: UpdateFieldDefinitionDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldDefinitionDto>({
      method: 'PUT',
      url: `/api/extract/field-definitions/${id}`,
      body: input,
    },
    { apiName: this.apiName,...config });
}