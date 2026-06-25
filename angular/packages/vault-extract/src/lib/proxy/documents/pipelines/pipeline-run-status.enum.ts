import { mapEnumToOptions } from '@abp/ng.core';

export enum PipelineRunStatus {
  Pending = 10,
  Running = 20,
  Succeeded = 30,
  Failed = 90,
  Skipped = 95,
}

export const pipelineRunStatusOptions = mapEnumToOptions(PipelineRunStatus);
