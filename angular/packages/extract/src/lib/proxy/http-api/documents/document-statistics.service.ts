import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DocumentStatisticsDto } from '../../documents/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentStatisticsService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  get = (config?: Partial<Rest.Config>) =>
    this.restService.request<any, DocumentStatisticsDto>({
      method: 'GET',
      url: '/api/extract/document-statistics',
    },
    { apiName: this.apiName,...config });
}