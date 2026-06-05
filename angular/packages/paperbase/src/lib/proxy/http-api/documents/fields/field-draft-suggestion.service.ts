import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DraftFieldDefinitionInput, FieldDefinitionDraftDto } from '../../../documents/fields/models';

@Injectable({
  providedIn: 'root',
})
export class FieldDraftSuggestionService {
  private restService = inject(RestService);
  apiName = 'Default';


  draft = (input: DraftFieldDefinitionInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldDefinitionDraftDto>({
      method: 'POST',
      url: '/api/paperbase/field-draft-suggestion/draft',
      body: input,
    },
    { apiName: this.apiName,...config });
}
