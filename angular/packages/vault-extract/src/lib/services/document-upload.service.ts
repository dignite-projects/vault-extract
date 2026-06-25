import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type { DocumentDto } from '../proxy/documents/models';

/**
 * Hand-written, proxy-external upload wrapper.
 *
 * Lives OUTSIDE `proxy/` so it survives `nx g @abp/ng.schematics:proxy-add` regeneration
 * (the generator overwrites everything under proxy/ — see proxy/README.md).
 *
 * Why not the generated `DocumentService.upload`? The schematic emits
 * `body: input.file` typed as `IRemoteStreamContent` (a metadata-only shape:
 * fileName/contentType/contentLength — no actual bytes) plus `cabinetId` as a query
 * param. That does not match the backend's multipart contract (form fields `File` +
 * `CabinetId`). File upload is the one endpoint the schematic can't express faithfully,
 * so we keep the known-good FormData call here.
 */
@Injectable({ providedIn: 'root' })
export class DocumentUploadService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';

  upload = (file: File, cabinetId?: string): Observable<DocumentDto> => {
    const formData = new FormData();
    formData.append('File', file, file.name);
    if (cabinetId) {
      formData.append('CabinetId', cabinetId);
    }
    return this.rest.request<FormData, DocumentDto>(
      { method: 'POST', url: '/api/extract/documents/upload', body: formData },
      { apiName: this.apiName },
    );
  };
}
