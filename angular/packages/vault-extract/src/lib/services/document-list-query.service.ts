import { PagedResultDto, Rest, RestService } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { DocumentListItemDto, GetDocumentListInput } from '../proxy/documents/models';
import { toDocumentListParams } from './document-list-query.params';

// #415 fix: hand-written, regeneration-safe wrapper for the document list GET.
//
// The generated DocumentService.getList mis-serializes the `fieldFilters` collection query param (it reaches
// the wire as "[object Object]", so the backend binds no filters — see document-list-query.params.ts). This
// wrapper hits the same endpoint but flattens fieldFilters into the indexed notation ASP.NET Core binds. It
// lives OUTSIDE proxy/ so `npm run generate-proxy` never overwrites it; the document list calls this instead
// of the generated getList. Drop it once/if the generator learns to serialize a complex collection query param.
@Injectable({ providedIn: 'root' })
export class DocumentListQueryService {
  private readonly restService = inject(RestService);
  apiName = 'Default';

  getList = (input: GetDocumentListInput, config?: Partial<Rest.Config>) =>
    this.restService.request<unknown, PagedResultDto<DocumentListItemDto>>(
      {
        method: 'GET',
        url: '/api/vault-extract/documents',
        params: toDocumentListParams(input),
      },
      { apiName: this.apiName, ...config },
    );
}
