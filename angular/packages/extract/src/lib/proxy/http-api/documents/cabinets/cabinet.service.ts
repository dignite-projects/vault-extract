import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { CabinetDto, CreateCabinetDto, UpdateCabinetDto } from '../../../documents/cabinets/models';

@Injectable({
  providedIn: 'root',
})
export class CabinetService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  create = (input: CreateCabinetDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, CabinetDto>({
      method: 'POST',
      url: '/api/extract/cabinets',
      body: input,
    },
    { apiName: this.apiName,...config });
  

  delete = (id: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, void>({
      method: 'DELETE',
      url: `/api/extract/cabinets/${id}`,
    },
    { apiName: this.apiName,...config });
  

  getList = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, CabinetDto[]>({
      method: 'GET',
      url: '/api/extract/cabinets',
    },
    { apiName: this.apiName,...config });
  

  update = (id: string, input: UpdateCabinetDto, config?: Partial<Rest.Config>) =>
    this.restService.request<any, CabinetDto>({
      method: 'PUT',
      url: `/api/extract/cabinets/${id}`,
      body: input,
    },
    { apiName: this.apiName,...config });
}