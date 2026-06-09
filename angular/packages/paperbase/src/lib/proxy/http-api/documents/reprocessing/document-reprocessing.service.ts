// NOTE (#289 步骤 6): 本文件 + ./reprocessing/models.ts + ./reprocessing/reclassification-scope.enum.ts
// 是手写的，严格匹配 `nx g @abp/ng.schematics:proxy-add` 的生成格式——因实现环境没有运行中的 host
// （生成器需 https://localhost:44348 在线）。当对运行中的 host 跑 `npm run generate-proxy` 时，会重新生成
// 出完全相同的内容（幂等无 diff）。后端契约见 IDocumentReprocessingAppService + DocumentReprocessingController。
import { RestService, Rest } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';
import type { FieldReextractionPreviewDto, ReclassificationPreviewDto, ReclassificationScopeInput, ReprocessingStartResultDto, StartFieldReextractionInput } from '../../../documents/reprocessing/models';

@Injectable({
  providedIn: 'root',
})
export class DocumentReprocessingService {
  private restService = inject(RestService);
  apiName = 'Default';


  previewFieldExtraction = (documentTypeId: string, config?: Partial<Rest.Config>) =>
    this.restService.request<any, FieldReextractionPreviewDto>({
      method: 'GET',
      url: '/api/paperbase/document-reprocessing/field-extraction/preview',
      params: { documentTypeId },
    },
    { apiName: this.apiName,...config });


  previewReclassification = (input: ReclassificationScopeInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReclassificationPreviewDto>({
      method: 'POST',
      url: '/api/paperbase/document-reprocessing/reclassification/preview',
      body: input,
    },
    { apiName: this.apiName,...config });


  startFieldExtraction = (input: StartFieldReextractionInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReprocessingStartResultDto>({
      method: 'POST',
      url: '/api/paperbase/document-reprocessing/field-extraction',
      body: input,
    },
    { apiName: this.apiName,...config });


  startReclassification = (input: ReclassificationScopeInput, config?: Partial<Rest.Config>) =>
    this.restService.request<any, ReprocessingStartResultDto>({
      method: 'POST',
      url: '/api/paperbase/document-reprocessing/reclassification',
      body: input,
    },
    { apiName: this.apiName,...config });
}
