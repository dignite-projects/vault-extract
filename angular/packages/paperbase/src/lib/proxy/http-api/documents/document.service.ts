import { Injectable, inject } from '@angular/core';
import { EnvironmentService, RestService } from '@abp/ng.core';
import type { PagedResultDto } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type { DocumentDto, DocumentListItemDto, GetDocumentListInput } from '../../documents/models';

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private readonly rest = inject(RestService);
  private readonly env = inject(EnvironmentService);
  private readonly apiName = 'Default';
  private readonly basePath = '/api/paperbase/documents';

  get = (id: string): Observable<DocumentDto> =>
    this.rest.request<void, DocumentDto>(
      { method: 'GET', url: `${this.basePath}/${id}` },
      { apiName: this.apiName }
    );

  getList = (input: GetDocumentListInput): Observable<PagedResultDto<DocumentListItemDto>> =>
    this.rest.request<void, PagedResultDto<DocumentListItemDto>>(
      {
        method: 'GET',
        url: this.basePath,
        params: {
          maxResultCount: input.maxResultCount ?? 10,
          skipCount: input.skipCount ?? 0,
          sorting: input.sorting,
          lifecycleStatus: input.lifecycleStatus ?? undefined,
          documentTypeCode: input.documentTypeCode ?? undefined,
          reviewStatus: input.reviewStatus ?? undefined,
          keyword: input.keyword ?? undefined,
          isDeleted: input.isDeleted ?? undefined,
          cabinetId: input.cabinetId ?? undefined,
        },
      },
      { apiName: this.apiName }
    );

  confirmClassification = (id: string, documentTypeCode: string): Observable<DocumentDto> =>
    this.rest.request<{ documentTypeCode: string }, DocumentDto>(
      {
        method: 'POST',
        url: `${this.basePath}/${id}/confirm-classification`,
        body: { documentTypeCode },
      },
      { apiName: this.apiName }
    );

  // Approve a PendingReview document. On the backend this is a no-op when the
  // document has no DocumentTypeCode yet (the operator must confirm/reclassify
  // a type first), so callers gate this action on documentTypeCode presence.
  approveReview = (id: string): Observable<DocumentDto> =>
    this.rest.request<void, DocumentDto>(
      { method: 'POST', url: `${this.basePath}/${id}/review/approve` },
      { apiName: this.apiName }
    );

  rejectReview = (id: string, reason?: string): Observable<DocumentDto> =>
    this.rest.request<{ reason?: string }, DocumentDto>(
      {
        method: 'POST',
        url: `${this.basePath}/${id}/review/reject`,
        body: { reason },
      },
      { apiName: this.apiName }
    );

  retryPipeline = (id: string, pipelineCode: string): Observable<void> =>
    this.rest.request<{ pipelineCode: string }, void>(
      {
        method: 'POST',
        url: `${this.basePath}/${id}/retry-pipeline`,
        body: { pipelineCode },
      },
      { apiName: this.apiName }
    );

  // Operator manual correction of extracted field values. Whole-replace:
  // pass the document's current field values (key = FieldDefinition.Name).
  updateExtractedFields = (id: string, fields: Record<string, unknown>): Observable<DocumentDto> =>
    this.rest.request<{ fields: Record<string, unknown> }, DocumentDto>(
      {
        method: 'POST',
        url: `${this.basePath}/${id}/extracted-fields`,
        body: { fields },
      },
      { apiName: this.apiName }
    );

  upload = (file: File, cabinetId?: string): Observable<DocumentDto> => {
    const formData = new FormData();
    formData.append('File', file, file.name);
    if (cabinetId) {
      formData.append('CabinetId', cabinetId);
    }
    return this.rest.request<FormData, DocumentDto>(
      {
        method: 'POST',
        url: `${this.basePath}/upload`,
        body: formData,
      },
      { apiName: this.apiName }
    );
  };

  delete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}` },
      { apiName: this.apiName }
    );

  permanentDelete = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'DELETE', url: `${this.basePath}/${id}/permanent` },
      { apiName: this.apiName }
    );

  restore = (id: string): Observable<void> =>
    this.rest.request<void, void>(
      { method: 'POST', url: `${this.basePath}/${id}/restore` },
      { apiName: this.apiName }
    );

  getBlob = (id: string): Observable<Blob> =>
    this.rest.request<void, Blob>(
      { method: 'GET', url: `${this.basePath}/${id}/blob`, responseType: 'blob' as any },
      { apiName: this.apiName }
    );

  getExportUrl = (input: GetDocumentListInput): string => {
    const params = new URLSearchParams();
    if (input.lifecycleStatus != null) params.set('lifecycleStatus', String(input.lifecycleStatus));
    if (input.documentTypeCode) params.set('documentTypeCode', input.documentTypeCode);
    if (input.reviewStatus != null) params.set('reviewStatus', String(input.reviewStatus));
    if (input.keyword) params.set('keyword', input.keyword);
    if (input.cabinetId) params.set('cabinetId', input.cabinetId);
    const qs = params.toString();
    return `${this.env.getApiUrl(this.apiName)}${this.basePath}/export${qs ? '?' + qs : ''}`;
  };
}
