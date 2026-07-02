import { Rest, RestService } from '@abp/ng.core';
import { Injectable, inject } from '@angular/core';

// #447: hand-written, regeneration-safe wrapper for the AI-polish endpoint (same pattern as
// document-upload.service). The typed proxy is produced against a running host (`npm run generate-proxy`);
// until then this calls the REST endpoint directly. After the next proxy regen it can be replaced by the
// generated FieldPromptPolishService. Lives OUTSIDE proxy/ so a regen never overwrites it.

export interface FieldPromptPolishInput {
  prompt: string;
}

export interface FieldPromptPolishResultDto {
  prompt: string;
}

@Injectable({ providedIn: 'root' })
export class FieldPromptPolishService {
  private readonly restService = inject(RestService);
  apiName = 'Default';

  polish = (input: FieldPromptPolishInput, config?: Partial<Rest.Config>) =>
    this.restService.request<FieldPromptPolishInput, FieldPromptPolishResultDto>(
      {
        method: 'POST',
        url: '/api/vault-extract/field-prompt-polish/polish',
        body: input,
      },
      { apiName: this.apiName, ...config },
    );
}
