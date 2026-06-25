import type { ExtensibleObject } from '@abp/ng.core';
import type { PipelineRunStatus } from './pipeline-run-status.enum';

export interface DocumentPipelineRunDto extends ExtensibleObject {
  id?: string;
  documentId?: string;
  pipelineCode?: string;
  status?: PipelineRunStatus;
  attemptNumber?: number;
  startedAt?: string;
  completedAt?: string | null;
  statusMessage?: string | null;
  candidates?: PipelineRunCandidate[] | null;
}

export interface PipelineRunCandidate {
  typeCode?: string;
  confidenceScore?: number;
}
