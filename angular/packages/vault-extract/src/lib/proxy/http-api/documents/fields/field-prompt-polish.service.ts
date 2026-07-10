import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { FieldPromptPolishInput, FieldPromptPolishResultDto } from '../../../documents/fields/models';

@Injectable({
  providedIn: 'root',
})
export class FieldPromptPolishService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  polish = (input: FieldPromptPolishInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldPromptPolishResultDto>({
      method: 'POST',
      url: '/api/vault-extract/field-prompt-polish/polish',
      body: input,
    },
    { apiName: this.apiName,...config });
}