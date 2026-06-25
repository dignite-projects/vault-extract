import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { SlugSuggestionDto, SuggestSlugInput } from '../../slugging/models';

@Injectable({
  providedIn: 'root',
})
export class SlugSuggestionService {
  private restService = inject(RestService);
  apiName = 'Default';
  

  suggest = (input: SuggestSlugInput, cancellationToken: any, config?: Partial<Rest.Config>) =>
    this.restService.request<any, SlugSuggestionDto>({
      method: 'POST',
      url: '/api/vault-extract/slug-suggestion/suggest',
      body: input,
    },
    { apiName: this.apiName,...config });
}