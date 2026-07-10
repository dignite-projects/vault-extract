import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { ExportDocumentsInput } from '../../../documents/exports/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentExportService {
  private restService = inject(RestService);
  apiName = 'Default';


  export = (input: ExportDocumentsInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, Blob>({
      method: 'POST',
      responseType: 'blob',
      url: '/api/vault-extract/documents/export',
      body: input,
    },
    { apiName: this.apiName,...config });
}
